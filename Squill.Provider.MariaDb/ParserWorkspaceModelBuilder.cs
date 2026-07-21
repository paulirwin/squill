using Squill.Core;
using Squill.MariaDbParser;
using Squill.MariaDbParser.Syntax;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Extracts a <see cref="Model"/> from declarative MariaDB SQL source by parsing it with the
/// ANTLR-based parser (no live database needed). Elements are built through
/// <see cref="MariaDbModelFactory"/> and named to match MariaDB's own auto-generated
/// constraint names (a <c>PRIMARY</c> key, <c>&lt;table&gt;_ibfk_N</c> foreign keys) so a
/// parsed model hash-matches one extracted from a live database.
/// </summary>
public class ParserWorkspaceModelBuilder : IDatabaseModelBuilder
{
    private readonly Workspace _workspace;
    private readonly IMariaDbParser _parser;

    public ParserWorkspaceModelBuilder(Workspace workspace, IMariaDbParser parser)
    {
        _workspace = workspace;
        _parser = parser;
    }

    public async Task<Model> ExtractModelAsync(CancellationToken cancellationToken = default)
    {
        var model = new Model();
        var validator = new SourceValidator();

        foreach (var file in _workspace.Files.Where(i => i.Kind == FileKind.Compile))
        {
            await ProcessFile(file, model, validator, cancellationToken);
        }

        // Validated after every file so declaration order (within and across files) does
        // not matter, just like it doesn't for the deployed schema.
        validator.ThrowIfInvalid();

        return model;
    }

    private async Task ProcessFile(IFile file,
        Model model,
        SourceValidator validator,
        CancellationToken cancellationToken)
    {
        var text = await file.ReadAllTextAsync(cancellationToken);

        Root root;
        try
        {
            root = _parser.Parse(text);
        }
        catch (MariaDbParseException ex)
        {
            throw new SqlSourceException(ex.Message, file.Name, ex.Line, ex.Column, innerException: ex);
        }

        foreach (var statement in root.Statements)
        {
            try
            {
                switch (statement)
                {
                    case CreateTableStatement createTable:
                        validator.AddCreateTable(file, createTable);

                        foreach (var element in MakeCreateTableElements(createTable))
                        {
                            model.Elements.Add(element);
                        }
                        break;

                    case CreateIndexStatement createIndex:
                        validator.AddCreateIndex(file, createIndex);

                        model.Elements.Add(MakeCreateIndexElement(createIndex));
                        break;
                }
            }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException
                or InvalidOperationException)
            {
                // Attach the source file and the statement's position so the host can
                // report the failure as a diagnostic pointing at the offending statement.
                throw new SqlSourceException(
                    ex.Message, file.Name, statement.Line, statement.Column, innerException: ex);
            }
        }
    }

    /// <summary>
    /// Validates that everything the source references is defined in the project — like
    /// SSDT, an unresolved reference is a build error reported at the referencing
    /// construct's source position. Own-table checks (constraint columns, FK shape) are
    /// made as statements are added; cross-object checks (referenced tables/columns) are
    /// deferred to <see cref="ThrowIfInvalid"/> so declaration order, within and across
    /// files, does not matter. Every error is reported, not just the first. MariaDB has
    /// no schema objects (a database is the schema), so tables are keyed by bare name.
    /// </summary>
    private sealed class SourceValidator
    {
        private readonly Dictionary<string, HashSet<string>> _declaredTables =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly List<TableReference> _tableReferences = [];
        private readonly List<SqlSourceException> _errors = [];

        // A deferred reference to a table (and optionally columns on it) that must be
        // declared somewhere in the project, with the source position to report against.
        private sealed record TableReference(
            string SourceFile,
            int? Line,
            int? Column,
            string Subject,
            string Table,
            IReadOnlyList<string> Columns);

        public void AddCreateTable(IFile file, CreateTableStatement createTable)
        {
            var table = createTable.Name.Name;

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var columnDefinition in createTable.Elements.OfType<ColumnDefinition>())
            {
                columns.Add(columnDefinition.Name.Name);
            }

            _declaredTables[table] = columns;

            foreach (var tableConstraint in createTable.Elements.OfType<TableConstraint>())
            {
                var constraint = tableConstraint is NamedTableConstraint named
                    ? named.Constraint
                    : tableConstraint;

                var line = constraint.Line ?? createTable.Line;
                var column = constraint.Column ?? createTable.Column;

                switch (constraint)
                {
                    case PrimaryKeyTableConstraint pk:
                        CheckOwnColumns(file, line, column,
                            $"Primary key on table '{table}'", table, columns,
                            pk.Columns.Select(c => c.Name));
                        break;

                    case UniqueKeyTableConstraint unique:
                        CheckOwnColumns(file, line, column,
                            $"Unique constraint on table '{table}'", table, columns,
                            unique.Columns.Select(c => c.Name));
                        break;

                    case IndexTableConstraint index:
                        CheckOwnColumns(file, line, column,
                            $"Index on table '{table}'", table, columns,
                            index.Columns.Select(c => c.Column.Name));
                        break;

                    case ForeignKeyTableConstraint fk:
                        CheckOwnColumns(file, line, column,
                            $"Foreign key on table '{table}'", table, columns,
                            fk.Columns.Select(c => c.Name));

                        if (fk.ReferencedColumns.Count > 0 && fk.ReferencedColumns.Count != fk.Columns.Count)
                        {
                            _errors.Add(new SqlSourceException(
                                $"Foreign key on table '{table}' has {fk.Columns.Count} referencing "
                                + $"column(s) but {fk.ReferencedColumns.Count} referenced column(s).",
                                file.Name, line, column, SqlSourceException.InvalidConstraint));
                        }

                        _tableReferences.Add(new TableReference(
                            file.Name, line, column,
                            $"Foreign key on table '{table}'",
                            fk.ReferencedTable.Name,
                            fk.ReferencedColumns.Select(c => c.Name).ToList()));
                        break;
                }
            }

            foreach (var columnDefinition in createTable.Elements.OfType<ColumnDefinition>())
            {
                foreach (var columnConstraint in columnDefinition.Constraints)
                {
                    var constraint = columnConstraint is NamedColumnConstraint named
                        ? named.Constraint
                        : columnConstraint;

                    if (constraint is ForeignKeyColumnConstraint fk)
                    {
                        _tableReferences.Add(new TableReference(
                            file.Name,
                            constraint.Line ?? createTable.Line,
                            constraint.Column ?? createTable.Column,
                            $"Foreign key on table '{table}'",
                            fk.ReferencedTable.Name,
                            fk.ReferencedColumn is { } referencedColumn
                                ? new[] { referencedColumn.Name }
                                : Array.Empty<string>()));
                    }
                }
            }
        }

        public void AddCreateIndex(IFile file, CreateIndexStatement createIndex)
        {
            _tableReferences.Add(new TableReference(
                file.Name, createIndex.Line, createIndex.Column,
                createIndex.Name is { } name ? $"Index '{name}'" : "Index",
                createIndex.OnTable.Name,
                createIndex.Columns.Select(c => c.Column.Name).ToList()));
        }

        public void ThrowIfInvalid()
        {
            foreach (var reference in _tableReferences)
            {
                if (!_declaredTables.TryGetValue(reference.Table, out var columns))
                {
                    _errors.Add(new SqlSourceException(
                        $"{reference.Subject} references table '{reference.Table}', "
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
                            $"{reference.Subject} references column '{reference.Table}.{column}', "
                            + "which is not defined in the project.",
                            reference.SourceFile, reference.Line, reference.Column,
                            SqlSourceException.UnresolvedReference));
                    }
                }
            }

            if (_errors.Count == 1)
            {
                throw _errors[0];
            }

            if (_errors.Count > 1)
            {
                throw new AggregateException(_errors);
            }
        }

        private void CheckOwnColumns(IFile file,
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
    }

    // A foreign key gathered while walking a CREATE TABLE, before it becomes an element (its
    // name may be explicit or derived from MariaDB's _ibfk_N convention).
    private sealed record ForeignKeySpec(
        string? ExplicitName,
        IReadOnlyList<string> Columns,
        QualifiedName ReferencedTable,
        IReadOnlyList<string> ReferencedColumns,
        ReferentialAction OnDelete,
        ReferentialAction OnUpdate);

    private static IEnumerable<Element> MakeCreateTableElements(CreateTableStatement createTable)
    {
        var tableName = TableName(createTable.Name);

        var tableElement = MariaDbModelFactory.CreateTable(tableName);

        var primaryKeyColumns = new List<MariaDbModelFactory.IndexedColumn>();
        var foreignKeys = new List<ForeignKeySpec>();
        var uniqueIndexes = new List<(string? Name, IReadOnlyList<string> Columns)>();

        AddColumns(tableElement, tableName, createTable, primaryKeyColumns, foreignKeys, uniqueIndexes);
        CollectTableLevelConstraints(createTable, tableName, primaryKeyColumns, foreignKeys, uniqueIndexes);

        yield return tableElement;

        // MariaDB always names the primary key 'PRIMARY'.
        if (primaryKeyColumns.Count > 0)
        {
            yield return MariaDbModelFactory.CreatePrimaryKey(
                tableName.Sibling("PRIMARY"), tableName, primaryKeyColumns);
        }

        // UNIQUE constraints/indexes become unique SqlIndex elements. MariaDB names a unique
        // index after its first column (uniquified with _2, _3, … on collision); with only
        // the common single-unique case in scope, use the first column's name.
        foreach (var (explicitName, columns) in uniqueIndexes)
        {
            var indexName = explicitName ?? columns[0];

            var indexedColumns = columns.Select(c =>
                new MariaDbModelFactory.IndexedColumn(tableName.Child(c), IsAscending: true));

            yield return MariaDbModelFactory.CreateIndex(
                tableName.Sibling(indexName), tableName, isUnique: true, indexMethod: "BTREE", indexedColumns);
        }

        // MariaDB (InnoDB) names an unnamed foreign key <table>_ibfk_N, numbered in
        // declaration order starting at 1.
        var ibfkOrdinal = 1;

        foreach (var foreignKey in foreignKeys)
        {
            var fkName = foreignKey.ExplicitName is { } explicitName
                ? tableName.Sibling(explicitName)
                : tableName.Sibling($"{tableName.UnqualifiedName}_ibfk_{ibfkOrdinal}");

            if (foreignKey.ExplicitName is null)
            {
                ibfkOrdinal++;
            }

            yield return MakeForeignKeyElement(tableName, fkName, foreignKey);
        }
    }

    private static Element MakeForeignKeyElement(SqlName tableName, SqlName fkName, ForeignKeySpec spec)
    {
        var referencedTable = TableName(spec.ReferencedTable);

        var columns = spec.Columns.Select(tableName.Child);
        var foreignColumns = spec.ReferencedColumns.Select(referencedTable.Child);

        return MariaDbModelFactory.CreateForeignKey(
            fkName,
            tableName,
            columns,
            referencedTable,
            foreignColumns,
            spec.OnDelete,
            spec.OnUpdate);
    }

    private static void CollectTableLevelConstraints(
        CreateTableStatement createTable,
        SqlName tableName,
        List<MariaDbModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys,
        List<(string? Name, IReadOnlyList<string> Columns)> uniqueIndexes)
    {
        foreach (var tableElement in createTable.Elements.OfType<TableConstraint>())
        {
            var (constraint, explicitName) = tableElement is NamedTableConstraint named
                ? (named.Constraint, named.Name)
                : (tableElement, (string?)null);

            switch (constraint)
            {
                case PrimaryKeyTableConstraint pk:
                    foreach (var column in pk.Columns)
                    {
                        primaryKeyColumns.Add(new MariaDbModelFactory.IndexedColumn(tableName.Child(column.Name)));
                    }
                    break;

                case UniqueKeyTableConstraint unique:
                    uniqueIndexes.Add((
                        explicitName ?? unique.IndexName,
                        unique.Columns.Select(c => c.Name).ToList()));
                    break;

                case ForeignKeyTableConstraint fk:
                    foreignKeys.Add(new ForeignKeySpec(
                        explicitName,
                        fk.Columns.Select(c => c.Name).ToList(),
                        fk.ReferencedTable,
                        fk.ReferencedColumns.Select(c => c.Name).ToList(),
                        fk.OnDelete ?? ReferentialAction.Restrict,
                        fk.OnUpdate ?? ReferentialAction.Restrict));
                    break;
            }
        }
    }

    private static void AddColumns(
        Element tableElement,
        SqlName tableName,
        CreateTableStatement createTable,
        List<MariaDbModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys,
        List<(string? Name, IReadOnlyList<string> Columns)> uniqueIndexes)
    {
        var columns = new Relationship(MariaDbRelationshipNames.Columns);
        tableElement.Relationships.Add(columns);

        foreach (var columnDefinition in createTable.Elements.OfType<ColumnDefinition>())
        {
            var columnName = tableName.Child(columnDefinition.Name.Name);

            var element = new Element(MariaDbElementTypes.SqlSimpleColumn)
            {
                Name = columnName,
            };

            bool? isNullable = null;
            bool isAutoIncrement = false;
            string? defaultValue = null;

            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                var (constraint, explicitName) = columnConstraint is NamedColumnConstraint named
                    ? (named.Constraint, named.Name)
                    : (columnConstraint, (string?)null);

                switch (constraint)
                {
                    case NullableColumnConstraint nullable:
                        isNullable = nullable.Nullable;
                        break;

                    case PrimaryKeyColumnConstraint:
                        primaryKeyColumns.Add(new MariaDbModelFactory.IndexedColumn(columnName));
                        // A PK column is implicitly NOT NULL.
                        isNullable = false;
                        break;

                    case UniqueKeyColumnConstraint:
                        uniqueIndexes.Add((null, new[] { columnDefinition.Name.Name }));
                        break;

                    case AutoIncrementColumnConstraint:
                        isAutoIncrement = true;
                        break;

                    case DefaultColumnConstraint defaultConstraint:
                        defaultValue = MariaDbDefaultValue.FromSourceToken(defaultConstraint.Token);
                        break;

                    case ForeignKeyColumnConstraint fk:
                        foreignKeys.Add(new ForeignKeySpec(
                            explicitName,
                            new[] { columnDefinition.Name.Name },
                            fk.ReferencedTable,
                            fk.ReferencedColumn is { } refCol ? new[] { refCol.Name } : Array.Empty<string>(),
                            fk.OnDelete ?? ReferentialAction.Restrict,
                            fk.OnUpdate ?? ReferentialAction.Restrict));
                        break;

                    // IgnoredColumnConstraint and others contribute nothing to the model.
                }
            }

            // Only a NOT NULL column stores IsNullable (=false); nullable is the default, so
            // an explicit NULL records no property — matching the DB extractor.
            if (isNullable is false)
            {
                element.Properties.Add(new Property(MariaDbPropertyNames.IsNullable, false));
            }

            if (isAutoIncrement)
            {
                element.Properties.Add(new Property(MariaDbPropertyNames.IsAutoIncrement, true));
            }

            if (defaultValue != null)
            {
                element.Properties.Add(new Property(MariaDbPropertyNames.DefaultValue, defaultValue));
            }

            element.Relationships.Add(BuildTypeSpecifier(columnDefinition.DataType));

            columns.Add(element);
        }
    }

    private static Relationship BuildTypeSpecifier(DataType dataType)
    {
        var typeSpec = new Element(MariaDbElementTypes.SqlTypeSpecifier)
        {
            Relationships =
            {
                new Relationship(MariaDbRelationshipNames.Type)
                {
                    new Reference(dataType.TypeName)
                    {
                        ExternalSource = "BuiltIns",
                    }
                }
            }
        };

        // A character type carries a single length modifier; a decimal type carries
        // precision and scale. These mirror the DB extractor so both sides hash-match.
        if (IsCharacterType(dataType.TypeName) && dataType.Modifiers.Count == 1)
        {
            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.Length, (int)dataType.Modifiers[0]));
        }
        else if (IsDecimalType(dataType.TypeName) && dataType.Modifiers.Count >= 1)
        {
            var precision = dataType.Modifiers[0];
            var scale = dataType.Modifiers.Count > 1 ? dataType.Modifiers[1] : 0;

            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.Precision, precision));
            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.Scale, scale));
        }

        if (dataType.IsUnsigned)
        {
            typeSpec.Properties.Add(new Property(MariaDbPropertyNames.IsUnsigned, true));
        }

        return new Relationship(MariaDbRelationshipNames.TypeSpecifier) { typeSpec };
    }

    private static Element MakeCreateIndexElement(CreateIndexStatement createIndex)
    {
        if (createIndex.Name is null)
        {
            throw new NotSupportedException("Unnamed CREATE INDEX statements are not supported.");
        }

        var tableName = TableName(createIndex.OnTable);
        var indexName = tableName.Sibling(createIndex.Name);

        // BTREE is the default index method for the common storage engines when USING is
        // omitted; defaulting to it matches the DB extractor, which reads BTREE from
        // information_schema for a standard index.
        var indexMethod = createIndex.IndexMethod ?? "BTREE";

        var columns = createIndex.Columns.Select(c =>
        {
            // MariaDB records a standard b-tree index column as ascending ('A') when no
            // direction is written; fill that in so a parsed index matches the extracted one.
            var isAscending = c.IsAscending ?? true;
            return new MariaDbModelFactory.IndexedColumn(tableName.Child(c.Column.Name), isAscending);
        });

        return MariaDbModelFactory.CreateIndex(
            indexName, tableName, createIndex.Unique, indexMethod, columns);
    }

    // Splits a (possibly db-qualified) table name into its bare table name. MariaDB objects
    // are not schema-scoped within a database, so a leading db qualifier is dropped: the
    // model is built for the connected database, mirroring the DB extractor which reads only
    // the current database's tables.
    private static SqlName TableName(QualifiedName qualifiedName)
        => SqlName.Object(qualifiedName.Name);

    private static bool IsCharacterType(string typeName)
        => typeName is "char" or "varchar";

    private static bool IsDecimalType(string typeName)
        => typeName is "decimal" or "numeric" or "dec" or "fixed";
}
