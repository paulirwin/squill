namespace Squill.Core;

/// <summary>
/// The provider-agnostic core of a parser-based build's source validator. It accumulates every
/// diagnostic (rather than throwing at the first) so one build reports all problems at once
/// (issue #61), tracks declared tables/columns and their origins, and defers cross-object checks
/// — unresolved table/column references and foreign-key backing-index uniqueness — until every
/// file has been seen, since a referenced object may be declared in any later file.
///
/// It is generic over the table-key type because the two providers key tables differently:
/// Postgres by a <c>(schema, table)</c> tuple, MariaDB by the bare table name. Each deferred
/// reference carries its own display name for messages (bare or schema-qualified), so the base
/// needs no rendering hook; the registration methods that read provider-specific syntax (schema
/// declarations, routine overloading, the constraint/index name namespace) stay in the subclass.
///
/// None of this affects element or property emission order — validation only throws or passes —
/// so it is safe to share despite the surrounding builder's hash-order sensitivity.
/// </summary>
public abstract class SourceValidatorBase<TTableKey>
    where TTableKey : notnull
{
    // The declared columns of each table (as a set for membership tests), and the same columns
    // in declaration order (for expanding a view's SELECT *). Keyed by the provider's table key.
    protected Dictionary<TTableKey, HashSet<string>> DeclaredTables { get; }
    protected Dictionary<TTableKey, List<string>> DeclaredColumnOrder { get; }

    // Where each table was first defined, so a redefinition can name the original's file/line.
    protected Dictionary<TTableKey, Origin> TableOrigins { get; }

    // The column sets that are unique within each table (its primary key plus any unique
    // constraint/index). A foreign key's referenced columns must match one of these exactly.
    protected Dictionary<TTableKey, List<HashSet<string>>> UniqueColumnSets { get; }
    protected HashSet<TTableKey> TablesWithPrimaryKey { get; }

    private readonly List<TableReference> _tableReferences = [];
    private readonly List<ForeignKeyUniquenessCheck> _foreignKeyChecks = [];
    private readonly List<SqlSourceException> _errors = [];

    protected SourceValidatorBase(IEqualityComparer<TTableKey>? keyComparer = null)
    {
        DeclaredTables = new(keyComparer);
        DeclaredColumnOrder = new(keyComparer);
        TableOrigins = new(keyComparer);
        UniqueColumnSets = new(keyComparer);
        TablesWithPrimaryKey = new(keyComparer);
    }

    /// <summary>Where an object was first defined, for a duplicate-definition message.</summary>
    protected sealed record Origin(string SourceFile, int? Line);

    /// <summary>
    /// A deferred reference to a table (and optionally columns on it) that must be declared
    /// somewhere in the project. <see cref="Key"/> is the resolved lookup key; <see cref="Display"/>
    /// is how the table is named in a diagnostic (bare or schema-qualified). Deferred so the
    /// referenced table may be declared in any later file.
    /// </summary>
    protected sealed record TableReference(
        string SourceFile,
        int? Line,
        int? Column,
        string Subject,
        TTableKey Key,
        string Display,
        IReadOnlyList<string> Columns);

    /// <summary>
    /// A deferred check that a foreign key's referenced columns are backed by a primary key or
    /// unique constraint on the referenced table. Deferred for the same reason as
    /// <see cref="TableReference"/>.
    /// </summary>
    protected sealed record ForeignKeyUniquenessCheck(
        string SourceFile,
        int? Line,
        int? Column,
        string Subject,
        TTableKey Key,
        string Display,
        IReadOnlyList<string> Columns);

    /// <summary>
    /// Records an error found outside the validator (a syntax error, or a statement that could
    /// not be mapped) so it is reported together with the reference errors instead of aborting
    /// the build at the first file.
    /// </summary>
    public void AddError(SqlSourceException error) => _errors.Add(error);

    /// <summary>Queues a deferred table/column reference to resolve once every file is seen.</summary>
    protected void AddTableReference(TableReference reference) => _tableReferences.Add(reference);

    /// <summary>Queues a deferred foreign-key backing-index check.</summary>
    protected void AddForeignKeyCheck(ForeignKeyUniquenessCheck check) => _foreignKeyChecks.Add(check);

    /// <summary>
    /// Records a column set made unique by a primary key or unique constraint/index on a table;
    /// a foreign key's referenced columns must match one of these exactly.
    /// </summary>
    protected void AddUniqueColumnSet(TTableKey table, IEnumerable<string> columns, bool isPrimaryKey)
    {
        if (!UniqueColumnSets.TryGetValue(table, out var sets))
        {
            sets = [];
            UniqueColumnSets[table] = sets;
        }

        sets.Add(new HashSet<string>(columns, StringComparer.OrdinalIgnoreCase));

        if (isPrimaryKey)
        {
            TablesWithPrimaryKey.Add(table);
        }
    }

    /// <summary>
    /// Reports each of <paramref name="columnNames"/> that is not declared on the table it
    /// belongs to (an own-column check, distinct from the cross-table reference resolution).
    /// </summary>
    protected void CheckOwnColumns(
        IFile file,
        int? line,
        int? column,
        string subject,
        string table,
        HashSet<string> declaredColumns,
        IEnumerable<string> columnNames)
    {
        foreach (var name in columnNames)
        {
            if (!declaredColumns.Contains(name))
            {
                _errors.Add(new SqlSourceException(
                    $"{subject} references column '{table}.{name}', "
                    + "which is not defined on the table.",
                    file.Name, line, column, SqlSourceException.UnresolvedReference));
            }
        }
    }

    /// <summary>Describes where an object was first defined, for a duplicate-definition message.</summary>
    protected static string DescribeOrigin(Origin origin)
        => origin.Line is { } line ? $"{origin.SourceFile} line {line}" : origin.SourceFile;

    /// <summary>
    /// Runs after every file: resolves the deferred table/column references and foreign-key
    /// backing-index checks, then throws if anything failed — a single exception for one error,
    /// an <see cref="AggregateException"/> for several, so one build surfaces every problem.
    ///
    /// A subclass with additional deferred references (Postgres's schema references) overrides
    /// this, does its extra resolution, and calls <c>base.ThrowIfInvalid()</c> last.
    /// </summary>
    public virtual void ThrowIfInvalid()
    {
        ResolveTableReferences();
        CheckForeignKeyUniqueness();
        ThrowAccumulated();
    }

    /// <summary>
    /// Resolves the deferred table/column references, reporting each unresolved table and each
    /// column that its resolved table does not declare.
    /// </summary>
    protected void ResolveTableReferences()
    {
        foreach (var reference in _tableReferences)
        {
            if (!DeclaredTables.TryGetValue(reference.Key, out var columns))
            {
                _errors.Add(new SqlSourceException(
                    $"{reference.Subject} references table '{reference.Display}', "
                    + "which is not defined in the project.",
                    reference.SourceFile, reference.Line, reference.Column,
                    SqlSourceException.UnresolvedReference));

                continue;
            }

            foreach (var column in reference.Columns)
            {
                if (!columns.Contains(column))
                {
                    _errors.Add(new SqlSourceException(
                        $"{reference.Subject} references column '{reference.Display}.{column}', "
                        + "which is not defined in the project.",
                        reference.SourceFile, reference.Line, reference.Column,
                        SqlSourceException.UnresolvedReference));
                }
            }
        }
    }

    /// <summary>
    /// Checks that every foreign key's referenced columns are backed by a primary key or unique
    /// constraint/index on the referenced table — both engines require this and otherwise fail
    /// the deploy. The columns are compared as a set, since a unique constraint on (a, b) equally
    /// covers a reference to (b, a).
    /// </summary>
    protected void CheckForeignKeyUniqueness()
    {
        foreach (var check in _foreignKeyChecks)
        {
            // An unresolved table was already reported; don't pile on.
            if (!DeclaredTables.TryGetValue(check.Key, out var declaredColumns))
            {
                continue;
            }

            // Likewise when a referenced column does not exist: that unresolved-reference error
            // is the specific one, and "not covered by a unique constraint" on top would be noise.
            if (check.Columns.Any(i => !declaredColumns.Contains(i)))
            {
                continue;
            }

            // No column list means "the referenced table's primary key", so it must have one.
            if (check.Columns.Count == 0)
            {
                if (!TablesWithPrimaryKey.Contains(check.Key))
                {
                    _errors.Add(new SqlSourceException(
                        $"{check.Subject} references table '{check.Display}', which has no "
                        + "primary key. Either declare a primary key on it or name the "
                        + "referenced columns explicitly.",
                        check.SourceFile, check.Line, check.Column,
                        SqlSourceException.InvalidConstraint));
                }

                continue;
            }

            var referenced = new HashSet<string>(check.Columns, StringComparer.OrdinalIgnoreCase);

            var backed = UniqueColumnSets.TryGetValue(check.Key, out var sets)
                && sets.Any(referenced.SetEquals);

            if (!backed)
            {
                _errors.Add(new SqlSourceException(
                    $"{check.Subject} references column(s) "
                    + $"({string.Join(", ", check.Columns)}) on table '{check.Display}', which "
                    + "are not covered by a primary key or unique constraint. Add a unique "
                    + "constraint or unique index on exactly those columns.",
                    check.SourceFile, check.Line, check.Column,
                    SqlSourceException.InvalidConstraint));
            }
        }
    }

    /// <summary>
    /// Throws the accumulated errors: nothing for none, the single error for one, an
    /// <see cref="AggregateException"/> for several. Called last by <see cref="ThrowIfInvalid"/>.
    /// </summary>
    protected void ThrowAccumulated()
    {
        if (_errors.Count == 1)
        {
            throw _errors[0];
        }

        if (_errors.Count > 1)
        {
            throw new AggregateException(_errors);
        }
    }
}
