using Squill.Core;
using Squill.PostgresParser;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

public class ParserWorkspaceModelBuilder : IDatabaseModelBuilder
{
    private readonly Workspace _workspace;
    private readonly IPostgresParser _postgresParser;

    public ParserWorkspaceModelBuilder(Workspace workspace, IPostgresParser postgresParser)
    {
        _workspace = workspace;
        _postgresParser = postgresParser;
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

        MoveProceduresToEnd(model);

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
            root = _postgresParser.Parse(text);
        }
        catch (PostgresParseException ex)
        {
            throw new SqlSourceException(ex.Message, file.Name, ex.Line, ex.Column, innerException: ex);
        }

        foreach (var statement in root.Statements)
        {
            try
            {
                ProcessStatement(statement, model, file, validator);
            }
            catch (Exception ex) when (ex is NotImplementedException or NotSupportedException
                or InvalidOperationException or PostgresParseException)
            {
                // Attach the source file and the statement's position so the host can
                // report the failure as a diagnostic pointing at the offending statement.
                throw new SqlSourceException(
                    ex.Message, file.Name, statement.Line, statement.Column, innerException: ex);
            }
        }
    }

    /// <summary>
    /// Moves procedures after every other element, ordered by schema, name and argument
    /// types. The database-extraction builder emits them last in exactly that order (the
    /// catalog has no notion of the order they were declared in), and the Merkle hash is
    /// order-sensitive — so a parsed model must adopt the same order to hash-match one
    /// extracted from a live database. Ordering them last also matches the create order,
    /// since a procedure body may reference any table.
    /// </summary>
    private static void MoveProceduresToEnd(Model model)
    {
        var procedures = model.Elements
            .Where(i => i.Type == PostgresElementTypes.SqlProcedure)
            .ToList();

        if (procedures.Count == 0)
        {
            return;
        }

        foreach (var procedure in procedures)
        {
            model.Elements.Remove(procedure);
        }

        // Ordinal, to match the database's byte-wise ordering of the same values.
        foreach (var procedure in procedures
                     .OrderBy(i => PostgresModelFactory.GetSchema(i), StringComparer.Ordinal)
                     .ThenBy(i => i.GetProperty<string>(PostgresPropertyNames.RoutineName), StringComparer.Ordinal)
                     .ThenBy(i => i.GetProperty<string>(PostgresPropertyNames.ArgumentTypes), StringComparer.Ordinal))
        {
            model.Elements.Add(procedure);
        }
    }

    private static void ProcessStatement(Statement statement,
        Model model,
        IFile file,
        SourceValidator validator)
    {
        if (statement is CreateTableStatement createTableStatement)
        {
            validator.AddCreateTable(file, createTableStatement);

            foreach (var element in MakeCreateTableElements(createTableStatement))
            {
                model.Elements.Add(element);
            }
        }
        else if (statement is CreateIndexStatement createIndexStatement)
        {
            validator.AddCreateIndex(file, createIndexStatement);

            var element = MakeCreateIndexElement(createIndexStatement);

            model.Elements.Add(element);
        }
        else if (statement is CreateExtensionStatement createExtensionStatement)
        {
            var element = MakeCreateExtensionElement(createExtensionStatement);

            model.Elements.Add(element);
        }
        else if (statement is CreateSchemaStatement createSchemaStatement)
        {
            validator.AddSchema(createSchemaStatement.Name.Name);

            // 'public' exists in every database by default and is not a declared
            // object, so a CREATE SCHEMA public is ignored — matching the DB-extraction
            // builder, which never emits a SqlSchema for public. Otherwise the two
            // models would never agree and a redeploy would never converge.
            if (!string.Equals(createSchemaStatement.Name.Name, "public", StringComparison.Ordinal))
            {
                model.Elements.Add(
                    PostgresModelFactory.CreateSchema(SqlName.Object(createSchemaStatement.Name.Name)));
            }
        }
        else if (statement is CreateProcedureStatement createProcedureStatement)
        {
            validator.AddCreateProcedure(file, createProcedureStatement);

            model.Elements.Add(MakeCreateProcedureElement(createProcedureStatement));
        }
        else if (statement is CreateFunctionStatement)
        {
            throw new NotImplementedException(
                "CREATE FUNCTION is not yet supported; only CREATE PROCEDURE is modeled");
        }
        else
        {
            throw new NotImplementedException(
                $"Statement type {statement.GetType()} to Element transformation not yet implemented");
        }
    }

    /// <summary>
    /// Validates that everything the source references is defined in the project — like
    /// SSDT, an unresolved reference is a build error reported at the referencing
    /// construct's source position. Own-table checks (constraint columns, FK shape) are
    /// made as statements are added; cross-object checks (referenced tables/columns,
    /// schemas) are deferred to <see cref="ThrowIfInvalid"/> so declaration order, within
    /// and across files, does not matter. Every error is reported, not just the first.
    /// </summary>
    private sealed class SourceValidator
    {
        private readonly Dictionary<(string Schema, string Table), HashSet<string>> _declaredTables = new();
        private readonly HashSet<string> _declaredSchemas = new(StringComparer.OrdinalIgnoreCase) { "public" };
        private readonly List<TableReference> _tableReferences = [];
        private readonly List<SchemaReference> _schemaReferences = [];
        private readonly List<SqlSourceException> _errors = [];

        // A deferred reference to a table (and optionally columns on it) that must be
        // declared somewhere in the project, with the source position to report against.
        private sealed record TableReference(
            string SourceFile,
            int? Line,
            int? Column,
            string Subject,
            string Schema,
            string Table,
            IReadOnlyList<string> Columns);

        // A deferred reference to a schema an object is declared in.
        private sealed record SchemaReference(
            string SourceFile,
            int? Line,
            int? Column,
            string Subject,
            string Schema);

        public void AddSchema(string name) => _declaredSchemas.Add(name);

        public void AddCreateProcedure(IFile file, CreateProcedureStatement createProcedureStatement)
        {
            var (schema, name) = SplitSchema(createProcedureStatement.Name);

            // Schemas are declared objects (issue #37): a procedure in a non-public schema
            // needs that schema's CREATE SCHEMA somewhere in the project, or the deploy
            // would fail — so its absence is a build error.
            if (!string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase))
            {
                _schemaReferences.Add(new SchemaReference(
                    file.Name, createProcedureStatement.Line, createProcedureStatement.Column,
                    $"Procedure '{schema}.{name.UnqualifiedName}'", schema));
            }
        }

        public void AddCreateTable(IFile file, CreateTableStatement createTableStatement)
        {
            var (schema, tableName) = SplitSchema(createTableStatement.Name);
            var table = tableName.UnqualifiedName;

            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
            {
                columns.Add(columnDefinition.Name.Name);
            }

            _declaredTables[TableKey(schema, table)] = columns;

            // Schemas are declared objects (issue #37): a table in a non-public schema
            // needs that schema's CREATE SCHEMA somewhere in the project or the deploy
            // would fail — so its absence is a build error.
            if (!string.Equals(schema, "public", StringComparison.OrdinalIgnoreCase))
            {
                _schemaReferences.Add(new SchemaReference(
                    file.Name, createTableStatement.Line, createTableStatement.Column,
                    $"Table '{schema}.{table}'", schema));
            }

            foreach (var tableConstraint in createTableStatement.Elements.OfType<TableConstraint>())
            {
                var constraint = tableConstraint is NamedTableConstraint named
                    ? named.Constraint
                    : tableConstraint;

                var line = constraint.Line ?? createTableStatement.Line;
                var column = constraint.Column ?? createTableStatement.Column;

                if (constraint is PrimaryKeyTableConstraint pk)
                {
                    CheckOwnColumns(file, line, column,
                        $"Primary key on table '{table}'", table, columns,
                        pk.Columns.Select(c => c.Name));
                }
                else if (constraint is ForeignKeyTableConstraint fk)
                {
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

                    AddForeignKeyReference(file, line, column, table,
                        fk.ReferencedTable, fk.ReferencedColumns.Select(c => c.Name).ToList());
                }
            }

            foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
            {
                foreach (var columnConstraint in columnDefinition.Constraints)
                {
                    var constraint = columnConstraint is NamedColumnConstraint named
                        ? named.Constraint
                        : columnConstraint;

                    if (constraint is ForeignKeyColumnConstraint fk)
                    {
                        AddForeignKeyReference(file,
                            constraint.Line ?? createTableStatement.Line,
                            constraint.Column ?? createTableStatement.Column,
                            table,
                            fk.ReferencedTable,
                            fk.ReferencedColumn is { } referencedColumn
                                ? new[] { referencedColumn.Name }
                                : Array.Empty<string>());
                    }
                }
            }
        }

        public void AddCreateIndex(IFile file, CreateIndexStatement createIndexStatement)
        {
            var (schema, tableName) = SplitSchema(createIndexStatement.OnRelation.Name);

            var subject = createIndexStatement.Name is { } name
                ? $"Index '{name.Name}'"
                : "Index";

            // Only plain column keys are checked; expression keys (not yet modeled) have
            // no single column to resolve.
            var columns = createIndexStatement.Elements
                .Select(e => e.Expression)
                .OfType<ColumnReferenceExpression>()
                .Select(c => c.Identifier.Name)
                .ToList();

            _tableReferences.Add(new TableReference(
                file.Name, createIndexStatement.Line, createIndexStatement.Column,
                subject, schema, tableName.UnqualifiedName, columns));
        }

        public void ThrowIfInvalid()
        {
            foreach (var reference in _schemaReferences)
            {
                if (!_declaredSchemas.Contains(reference.Schema))
                {
                    _errors.Add(new SqlSourceException(
                        $"{reference.Subject} is in schema '{reference.Schema}', "
                        + "which is not defined in the project.",
                        reference.SourceFile, reference.Line, reference.Column,
                        SqlSourceException.UnresolvedReference));
                }
            }

            foreach (var reference in _tableReferences)
            {
                var display = reference.Schema == "public"
                    ? reference.Table
                    : $"{reference.Schema}.{reference.Table}";

                if (!_declaredTables.TryGetValue(
                        TableKey(reference.Schema, reference.Table), out var columns))
                {
                    _errors.Add(new SqlSourceException(
                        $"{reference.Subject} references table '{display}', "
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
                            $"{reference.Subject} references column '{display}.{column}', "
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

        private void AddForeignKeyReference(IFile file,
            int? line,
            int? column,
            string referencingTable,
            QualifiedName referencedTable,
            IReadOnlyList<string> referencedColumns)
        {
            var (referencedSchema, referencedName) = SplitSchema(referencedTable);

            _tableReferences.Add(new TableReference(
                file.Name, line, column,
                $"Foreign key on table '{referencingTable}'",
                referencedSchema, referencedName.UnqualifiedName, referencedColumns));
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

        // Postgres folds unquoted identifiers to lowercase while the parser preserves
        // source casing, so declared-object lookups compare case-insensitively — this can
        // miss a quoted-identifier case mismatch, but never produces a false error.
        private static (string Schema, string Table) TableKey(string schema, string table)
            => (schema.ToLowerInvariant(), table.ToLowerInvariant());
    }

    // A foreign key gathered while walking a CREATE TABLE, before it becomes an
    // element (its name may be explicit or derived from the Postgres convention).
    private sealed record ForeignKeySpec(
        string? ExplicitName,
        IReadOnlyList<string> Columns,
        QualifiedName ReferencedTable,
        IReadOnlyList<string> ReferencedColumns,
        ReferentialAction OnDelete,
        ReferentialAction OnUpdate);

    private static IEnumerable<Element> MakeCreateTableElements(CreateTableStatement createTableStatement)
    {
        var (schema, tableName) = SplitSchema(createTableStatement.Name);

        // The table element is built through the factory so it carries the same schema
        // relationship the DB builder emits (an implicit "public" when the CREATE TABLE
        // is not schema-qualified), letting a parsed model hash-match an extracted one.
        var tableElement = PostgresModelFactory.CreateTable(tableName, schema);

        var primaryKeyColumns = new List<PostgresModelFactory.IndexedColumn>();
        var foreignKeys = new List<ForeignKeySpec>();

        var inlinePkName = AddTableColumnsRelationship(
            tableElement, tableName, createTableStatement, primaryKeyColumns, foreignKeys);

        var tableLevelPkName = CollectTableLevelConstraints(
            createTableStatement, tableName, primaryKeyColumns, foreignKeys);

        // A named PK can be written inline on its column (CONSTRAINT pk_x PRIMARY KEY) or as
        // a table-level clause; at most one applies, so either source is the explicit name.
        var explicitPkName = inlinePkName ?? tableLevelPkName;

        yield return tableElement;

        // Postgres names an unnamed primary key <table>_pkey. The PK is emitted as its
        // own model element (not a table annotation) so both builders agree and so it is
        // visible to schema comparison and scripting.
        if (primaryKeyColumns.Count > 0)
        {
            var pkName = tableName.Sibling(explicitPkName ?? $"{tableName.UnqualifiedName}_pkey");

            yield return PostgresModelFactory.CreatePrimaryKey(pkName, tableName, primaryKeyColumns);
        }

        foreach (var foreignKey in foreignKeys)
        {
            yield return MakeForeignKeyElement(tableName, foreignKey);
        }
    }

    private static Element MakeForeignKeyElement(SqlName tableName, ForeignKeySpec spec)
    {
        var referencedTable = NormalizeReferencedTable(spec.ReferencedTable);

        // Postgres derives an unnamed FK constraint's name as <table>_<firstcolumn>_fkey.
        // Predicting it here lets a parsed model hash-match one extracted from the DB.
        var fkName = spec.ExplicitName is { } explicitName
            ? tableName.Sibling(explicitName)
            : tableName.Sibling($"{tableName.UnqualifiedName}_{spec.Columns[0]}_fkey");

        var columns = spec.Columns.Select(tableName.Child);
        var foreignColumns = spec.ReferencedColumns.Select(referencedTable.Child);

        return PostgresModelFactory.CreateForeignKey(
            fkName,
            tableName,
            columns,
            referencedTable,
            foreignColumns,
            spec.OnDelete,
            spec.OnUpdate);
    }

    // Walks the table-level constraints, appending table-level PK columns and FK specs.
    // Returns the explicit name of a table-level PRIMARY KEY constraint if one was named.
    private static string? CollectTableLevelConstraints(CreateTableStatement createTableStatement,
        SqlName tableName,
        List<PostgresModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys)
    {
        string? explicitPkName = null;

        foreach (var tableConstraint in createTableStatement.Elements.OfType<TableConstraint>())
        {
            var (constraint, explicitName) = tableConstraint is NamedTableConstraint named
                ? (named.Constraint, named.Name.Name)
                : (tableConstraint, (string?)null);

            if (constraint is PrimaryKeyTableConstraint pk)
            {
                foreach (var column in pk.Columns)
                {
                    primaryKeyColumns.Add(new PostgresModelFactory.IndexedColumn(tableName.Child(column.Name)));
                }

                explicitPkName = explicitName;
            }
            else if (constraint is ForeignKeyTableConstraint fk)
            {
                foreignKeys.Add(new ForeignKeySpec(
                    explicitName,
                    fk.Columns.Select(c => c.Name).ToList(),
                    fk.ReferencedTable,
                    fk.ReferencedColumns.Select(c => c.Name).ToList(),
                    fk.OnDelete ?? ReferentialAction.NoAction,
                    fk.OnUpdate ?? ReferentialAction.NoAction));
            }
        }

        return explicitPkName;
    }

    // Returns the explicit name of a single-column inline PRIMARY KEY constraint
    // (CONSTRAINT pk_x PRIMARY KEY on a column), or null if the PK is unnamed or table-level.
    private static string? AddTableColumnsRelationship(Element sqlTableElement,
        SqlName tableName,
        CreateTableStatement createTableStatement,
        List<PostgresModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys)
    {
        string? inlinePkName = null;

        var columns = new Relationship(PostgresRelationshipNames.Columns);
        sqlTableElement.Relationships.Add(columns);

        foreach (var columnDefinition in createTableStatement.Elements.OfType<ColumnDefinition>())
        {
            var columnName = tableName.Child(columnDefinition.Name.Name);

            var element = new Element(PostgresElementTypes.SqlSimpleColumn)
            {
                Name = columnName,
            };

            // Nullability can be implied by several constraints (NOT NULL, PRIMARY KEY,
            // IDENTITY); collect these here and emit the properties once, after the loop,
            // in a fixed order (IsNullable then identity) so the model matches the
            // DB-extraction builder's property order — the Merkle hash is order-sensitive.
            bool? isNullable = null;
            IdentityColumnConstraint? identityConstraint = null;
            string? defaultValue = null;

            foreach (var columnConstraint in columnDefinition.Constraints)
            {
                // A CONSTRAINT <name> wrapper carries an explicit name; unwrap it but
                // remember the name for constraints (like FKs) that model it.
                var (constraint, explicitName) = columnConstraint is NamedColumnConstraint named
                    ? (named.Constraint, named.Name)
                    : (columnConstraint, (string?)null);

                if (constraint is NullableColumnConstraint nullableColumnConstraint)
                {
                    isNullable = nullableColumnConstraint.Nullable;
                }
                else if (constraint is PrimaryKeyColumnConstraint)
                {
                    primaryKeyColumns.Add(new PostgresModelFactory.IndexedColumn(columnName));

                    // A CONSTRAINT <name> PRIMARY KEY on the column names the PK; carry it
                    // so it survives scripting rather than being replaced by <table>_pkey.
                    if (explicitName != null)
                    {
                        inlinePkName = explicitName;
                    }

                    // PKs are not nullable
                    isNullable = false;
                }
                else if (constraint is DefaultColumnConstraint defaultConstraint)
                {
                    // Postgres accepts CONSTRAINT <name> DEFAULT <expr> but does not persist
                    // the name (a column default is not a named constraint as in SQL Server),
                    // so it could never survive a round-trip from the database. Reject it
                    // with a clear message rather than model a name that would vanish.
                    if (explicitName != null)
                    {
                        throw new NotSupportedException(
                            $"Column '{columnName}' has a named DEFAULT constraint "
                            + $"('{explicitName}'), but PostgreSQL does not store a name for a "
                            + "column default; remove the constraint name.");
                    }

                    // Only constant literals are modeled (int/numeric/bool/string); a
                    // function default (now(), a serial's nextval, …) or DEFAULT NULL is
                    // left off the model. See PostgresDefaultValue.
                    defaultValue = PostgresDefaultValue.FromExpression(defaultConstraint.Expression);
                }
                else if (constraint is IdentityColumnConstraint identity)
                {
                    identityConstraint = identity;

                    // Postgres identity columns are implicitly NOT NULL.
                    isNullable = false;
                }
                else if (constraint is ForeignKeyColumnConstraint fk)
                {
                    foreignKeys.Add(new ForeignKeySpec(
                        explicitName,
                        new[] { columnDefinition.Name.Name },
                        fk.ReferencedTable,
                        fk.ReferencedColumn is { } refCol ? new[] { refCol.Name } : Array.Empty<string>(),
                        fk.OnDelete ?? ReferentialAction.NoAction,
                        fk.OnUpdate ?? ReferentialAction.NoAction));
                }
                else
                {
                    throw new NotImplementedException(
                        $"Column constraint type {constraint.GetType()} to property mapping not implemented");
                }
            }

            // Only a NOT NULL column stores IsNullable (=false). Nullable is the default,
            // so an explicit `NULL` records no property — matching the DB-extraction
            // builder, which likewise omits the property for nullable columns. Storing
            // IsNullable=true would make a parsed model diverge from the extracted one and
            // break the hash-based comparison.
            if (isNullable is false)
            {
                element.Properties.Add(new Property(PostgresPropertyNames.IsNullable, false));
            }

            if (identityConstraint is not null)
            {
                element.Properties.Add(new Property(PostgresPropertyNames.IsIdentity, true));
                element.Properties.Add(new Property(PostgresPropertyNames.IdentityGeneration,
                    identityConstraint.Always ? "Always" : "ByDefault"));

                AddIdentitySequenceOptionProperties(element, identityConstraint, columnDefinition.DataType);
            }

            // Emitted after identity so parsed and DB-extracted models add the property in
            // the same order (the Merkle hash is order-sensitive).
            if (defaultValue != null)
            {
                element.Properties.Add(new Property(PostgresPropertyNames.DefaultValue, defaultValue));
            }

            if (columnDefinition.DataType is BuiltInDataType builtInDataType)
            {
                var typeSpec = new Element(PostgresElementTypes.SqlTypeSpecifier)
                {
                    Relationships =
                    {
                        new Relationship(PostgresRelationshipNames.Type)
                        {
                            new Reference(builtInDataType.Type.CanonicalName())
                            {
                                ExternalSource = "BuiltIns",
                            }
                        }
                    }
                };

                // A type with no modifiers gets no length/precision properties. For a
                // bare varchar this mirrors the DB builder, where an unbounded
                // character varying reports character_maximum_length = NULL — so both
                // sides agree and the model hashes match (issue #6).
                if (builtInDataType.Modifiers.Count == 1)
                {
                    if (builtInDataType.Type is PostgresBuiltInDataType.Varchar or PostgresBuiltInDataType.Char)
                    {
                        if (builtInDataType.Modifiers[0] is not LiteralExpression { Value: long length})
                        {
                            throw new InvalidOperationException("Unexpected length modifier for varchar or character type");
                        }

                        // Store as int to match the DB-extraction builder and the script
                        // generator, which both use int for the Length property.
                        typeSpec.Properties.Add(new Property(PostgresPropertyNames.Length, (int)length));
                    }
                    else
                    {
                        throw new NotImplementedException(
                            $"Modifiers for built-in data type {builtInDataType.Type} not yet implemented");
                    }
                }
                else if (builtInDataType.Modifiers.Count > 1)
                {
                    if (builtInDataType.Type == PostgresBuiltInDataType.Decimal)
                    {
                        if (builtInDataType.Modifiers.Count != 2)
                        {
                            throw new InvalidOperationException("Expected only 2 modifiers for numeric/decimal type");
                        }

                        if (builtInDataType.Modifiers[0] is not LiteralExpression { Value: long precision }
                            || builtInDataType.Modifiers[1] is not LiteralExpression { Value: long scale })
                        {
                            throw new InvalidOperationException(
                                "Either precision or scale modifier for numeric/decimal type was not an integer");
                        }
                        
                        typeSpec.Properties.Add(new Property(PostgresPropertyNames.Precision, precision));
                        typeSpec.Properties.Add(new Property(PostgresPropertyNames.Scale, scale));
                    }
                    else
                    {
                        throw new NotImplementedException($"More than 1 modifier not yet implemented for built-in type {builtInDataType.Type}");
                    }
                }
                
                element.Relationships.Add(new Relationship(PostgresRelationshipNames.TypeSpecifier)
                {
                    typeSpec
                });
            }
            else if (columnDefinition.DataType is UnresolvedDataType unresolvedDataType)
            {
                // A custom type (e.g. pgvector's `vector`) is not a built-in. Its type
                // name is carried verbatim so it hash-matches the DB builder, which reads
                // the same name from pg_type (udt_name). A single integer modifier — the
                // dimension in vector(3) — is stored as Length, mirroring how the DB
                // builder reports it from atttypmod.
                var typeSpec = new Element(PostgresElementTypes.SqlTypeSpecifier)
                {
                    Relationships =
                    {
                        new Relationship(PostgresRelationshipNames.Type)
                        {
                            new Reference(unresolvedDataType.TypeName)
                            {
                                ExternalSource = "BuiltIns",
                            }
                        }
                    }
                };

                if (unresolvedDataType.Modifiers.Count == 1)
                {
                    if (unresolvedDataType.Modifiers[0] is not LiteralExpression { Value: long dimension })
                    {
                        throw new NotImplementedException(
                            $"Non-integer modifier for custom type {unresolvedDataType.TypeName} not yet implemented");
                    }

                    typeSpec.Properties.Add(new Property(PostgresPropertyNames.Length, (int)dimension));
                }
                else if (unresolvedDataType.Modifiers.Count > 1)
                {
                    throw new NotImplementedException(
                        $"More than one modifier not yet implemented for custom type {unresolvedDataType.TypeName}");
                }

                element.Relationships.Add(new Relationship(PostgresRelationshipNames.TypeSpecifier)
                {
                    typeSpec
                });
            }
            else
            {
                throw new NotImplementedException(
                    $"Data meta-type {columnDefinition.DataType.GetType()} to relationship mapping not implemented");
            }
            
            columns.Add(element);
        }

        return inlinePkName;
    }

    // Emits the identity sequence-option properties (issue #13) in a fixed order —
    // StartValue, Increment, MinValue, MaxValue, CacheSize, IsCycling — omitting any
    // option equal to the Postgres default for the column's type and sequence direction,
    // so the model hash-matches a DB extraction (which reports defaults filled in).
    private static void AddIdentitySequenceOptionProperties(
        Element element, IdentityColumnConstraint identity, DataType dataType)
    {
        var canonicalType = dataType is BuiltInDataType builtIn
            ? builtIn.Type.CanonicalName()
            : "integer";

        var increment = identity.Increment ?? PostgresIdentitySequenceDefaults.Increment;
        var (defaultStart, defaultMin, defaultMax) =
            PostgresIdentitySequenceDefaults.For(canonicalType, increment);

        if (identity.StartValue is { } startValue && startValue != defaultStart)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.StartValue, startValue));
        }

        if (increment != PostgresIdentitySequenceDefaults.Increment)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.Increment, increment));
        }

        if (identity.MinValue is { } minValue && minValue != defaultMin)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.MinValue, minValue));
        }

        if (identity.MaxValue is { } maxValue && maxValue != defaultMax)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.MaxValue, maxValue));
        }

        if (identity.CacheSize is { } cacheSize && cacheSize != PostgresIdentitySequenceDefaults.CacheSize)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.CacheSize, cacheSize));
        }

        if (identity.Cycle is { } cycle && cycle != PostgresIdentitySequenceDefaults.IsCycling)
        {
            element.Properties.Add(new Property(PostgresPropertyNames.IsCycling, cycle));
        }
    }

    private static Element MakeCreateIndexElement(CreateIndexStatement createIndexStatement)
    {
        if (createIndexStatement.Name is null)
        {
            // TODO.PI: determine anonymous index naming convention for Postgres
            throw new NotImplementedException("Unnamed CREATE INDEX statements are not yet supported");
        }

        if (createIndexStatement.OnRelation.Only || createIndexStatement.OnRelation.Star)
        {
            throw new NotImplementedException("ONLY and descendant table syntax on CREATE INDEX are not yet supported");
        }

        // The table an index is ON may be schema-qualified (ON staging.film); split off the
        // schema so the table reference, column references, and index name are all bare —
        // matching the DB-extraction builder — with the schema carried separately.
        var (schema, tableName) = SplitSchema(createIndexStatement.OnRelation.Name);

        var indexName = SqlName.Object(createIndexStatement.Name.Name);

        // btree is Postgres's implicit access method when USING is omitted. Defaulting to
        // it here (rather than leaving the method null) matches the DB builder, which reads
        // "btree" from pg_am — so a parsed index and one extracted from the database agree.
        var indexMethod = createIndexStatement.UsingMethod?.Name ?? "btree";

        // Only btree carries per-column ASC/DESC and NULLS ordering; other access methods
        // (e.g. hnsw) reject those options and the DB builder omits them. So the implicit
        // ASC / NULLS LAST defaults are only filled in for a btree index, keeping both
        // builders' models identical.
        var isBtree = string.Equals(indexMethod, "btree", StringComparison.OrdinalIgnoreCase);

        var columns = new List<PostgresModelFactory.IndexedColumn>();

        foreach (var indexElement in createIndexStatement.Elements)
        {
            if (indexElement.Expression is not ColumnReferenceExpression columnReference)
            {
                throw new NotImplementedException(
                    $"Index element expression type {indexElement.Expression.GetType()} to element mapping not implemented");
            }

            // When a btree index does not spell out a direction / null-order, Postgres
            // applies ASC (IsAscending = true) and NULLS LAST (NullsFirst = false); the DB
            // builder records those defaults, so the parser fills them in too.
            bool? isAscending = indexElement.Direction is IndexElementDirection direction
                ? direction == IndexElementDirection.Asc
                : isBtree ? true : null;

            bool? nullsFirst = indexElement.NullOrder is IndexElementNullOrder nullOrder
                ? nullOrder == IndexElementNullOrder.NullsFirst
                : isBtree ? false : null;

            columns.Add(new PostgresModelFactory.IndexedColumn(
                tableName.Child(columnReference.Identifier.Name),
                isAscending,
                nullsFirst,
                indexElement.OperatorClass?.Name));
        }

        // A WHERE clause makes this a partial (filtered) index; render its predicate
        // back to SQL text so it can be carried in the model and re-emitted on publish.
        var filterPredicate = createIndexStatement.WhereClause is { } whereClause
            ? ExpressionSqlRenderer.Render(whereClause)
            : null;

        // WITH (...) storage parameters (e.g. HNSW's m / ef_construction). Rendered to a
        // canonical string so parsed and DB-extracted models hash-match.
        var storageParameters = createIndexStatement.WithOptions.Count > 0
            ? RenderStorageParameters(createIndexStatement.WithOptions)
            : null;

        // NOTE: CONCURRENTLY and IF NOT EXISTS affect how the index gets created, not the desired schema state
        return PostgresModelFactory.CreateIndex(
            indexName,
            tableName,
            createIndexStatement.Unique,
            indexMethod,
            columns,
            filterPredicate,
            storageParameters,
            schema);
    }

    private static Element MakeCreateExtensionElement(CreateExtensionStatement createExtensionStatement)
    {
        // Extensions are database-level, standalone objects identified by name.
        // The declared version (if any) is carried through; SCHEMA is not yet modeled.
        var extensionName = SqlName.Object(createExtensionStatement.Name.Name);

        return PostgresModelFactory.CreateExtension(extensionName, createExtensionStatement.Version);
    }

    // Renders index storage parameters (the WITH clause) to a canonical
    // "name=value, name=value" string, preserving declaration order. This is the same
    // shape the DB builder produces from pg_class.reloptions, so a parsed index and one
    // extracted from the database hash-match.
    private static string RenderStorageParameters(IEnumerable<IndexWithOption> options)
        => string.Join(", ", options.Select(o => o.Value is null ? o.Name : $"{o.Name}={o.Value}"));

    private static SqlName ToSqlName(QualifiedName qualifiedName)
        => SqlName.Object(qualifiedName.Segments.Select(i => i.Name).ToArray());

    // Normalizes a foreign key's referenced-table name to the convention both builders
    // share: bare when it resolves to the public schema (matching an unqualified source
    // reference and the DB builder's bare public names), schema-qualified otherwise (so a
    // cross-schema FK round-trips). e.g. `public.book` -> book; `audit.log` -> audit.log.
    private static SqlName NormalizeReferencedTable(QualifiedName qualifiedName)
    {
        var (schema, name) = SplitSchema(qualifiedName);

        return schema == "public" ? name : SqlName.Object(schema, name.UnqualifiedName);
    }

    // Splits a (possibly schema-qualified) table name into its schema and its bare,
    // schema-less name, mirroring how the DB builder stores tables: the element Name is
    // the bare table name (e.g. "film") and the schema is carried as a separate Schema
    // relationship. A CREATE TABLE with no schema qualifier defaults to "public", which
    // is where Postgres puts it — so both builders agree.
    /// <summary>
    /// PostgreSQL's internal type-name aliases mapped to the canonical spelling
    /// format_type() reports. Taken from pg_catalog, where these are the base types whose
    /// typname differs from their formatted name.
    ///
    /// The 1-byte internal <c>"char"</c> type is deliberately absent: the grammar already
    /// resolves a written <c>char</c> to <c>character</c>, and mapping it here would turn
    /// <c>char(3)</c> into the wrong type.
    /// </summary>
    private static readonly Dictionary<string, string> PostgresTypeAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["bool"] = "boolean",
            ["bpchar"] = "character",
            ["float4"] = "real",
            ["float8"] = "double precision",
            ["int2"] = "smallint",
            ["int4"] = "integer",
            ["int8"] = "bigint",
            ["time"] = "time without time zone",
            ["timestamp"] = "timestamp without time zone",
            ["timestamptz"] = "timestamp with time zone",
            ["timetz"] = "time with time zone",
            ["varbit"] = "bit varying",
            ["varchar"] = "character varying",
        };

    private static Element MakeCreateProcedureElement(CreateProcedureStatement statement)
    {
        var (schema, name) = SplitSchema(statement.Name);

        if (statement.Language is not { } language)
        {
            throw new InvalidOperationException("A procedure must declare a LANGUAGE");
        }

        if (statement.Body is not { } body)
        {
            throw new InvalidOperationException("A procedure must declare a body");
        }

        // The parameter type is stored normalized (modifiers discarded) rather than as
        // written, because that is all PostgreSQL retains for a routine parameter — a
        // `varchar(10)` parameter is not length-checked, and the catalog reports it as
        // plain `character varying`. Storing the declared form would mean a parsed model
        // could never hash-match an extracted one.
        // A parameter DEFAULT is not modeled: PostgreSQL rewrites the expression when it
        // stores it (a bare 'x' comes back as 'x'::text), so a parsed model could not
        // hash-match an extracted one. Rejecting it keeps the gap explicit rather than
        // silently deploying a procedure that loses its defaults.
        if (statement.Parameters.FirstOrDefault(i => i.DefaultExpression is not null) is { } withDefault)
        {
            throw new NotImplementedException(
                $"A DEFAULT on procedure parameter '{withDefault.Name?.Name ?? "(unnamed)"}' "
                + "is not yet supported");
        }

        var parameters = statement.Parameters
            .Select(parameter => new PostgresModelFactory.ProcedureParameter(
                RenderParameterMode(parameter.Mode),
                parameter.Name?.Name,
                NormalizeArgumentType(parameter.DataType)))
            .ToList();

        // Only IN and INOUT parameters form a procedure's identity, matching
        // pg_proc.proargtypes — which is what the DB-extraction builder reads back.
        var argumentTypes = string.Join(',', statement.Parameters
            .Where(i => i.Mode is ParameterMode.In or ParameterMode.InOut or ParameterMode.Variadic)
            .Select(i => NormalizeArgumentType(i.DataType)));

        return PostgresModelFactory.CreateProcedure(
            schema,
            name.UnqualifiedName,
            argumentTypes,
            language,
            body,
            parameters,
            statement.SecurityDefiner);
    }

    private static string RenderParameterMode(ParameterMode mode) => mode switch
    {
        ParameterMode.In => "IN",
        ParameterMode.Out => "OUT",
        ParameterMode.InOut => "INOUT",
        ParameterMode.Variadic => "VARIADIC",
        _ => throw new NotImplementedException($"Parameter mode {mode} is not supported"),
    };

    /// <summary>
    /// Renders a parameter's type the way PostgreSQL records it in a routine's argument
    /// signature: the canonical type name with all modifiers discarded, so `varchar(10)`
    /// becomes `character varying` and `numeric(5,2)` becomes `numeric`. This must match
    /// format_type() exactly, since the DB-extraction builder reads the signature back
    /// through it and the two models are compared by hash.
    /// </summary>
    private static string NormalizeArgumentType(DataType dataType)
    {
        if (dataType is ArrayDataType arrayDataType)
        {
            return $"{NormalizeArgumentType(arrayDataType.ElementType)}[]";
        }

        if (dataType is not BuiltInDataType builtIn)
        {
            var typeName = dataType.TypeName.ToLowerInvariant();

            // PostgreSQL's internal type aliases are not recognized as built-ins by the
            // grammar, but format_type always reports the canonical spelling — so map them
            // here or a signature written with an alias would never hash-match.
            // See https://www.postgresql.org/docs/current/datatype.html
            return PostgresTypeAliases.TryGetValue(typeName, out var canonical) ? canonical : typeName;
        }

        // A bare `timestamp`/`time` is spelled out in full in an argument signature, unlike
        // in a column type specifier where the short form is what the catalog reports.
        return builtIn.Type switch
        {
            PostgresBuiltInDataType.Timestamp => "timestamp without time zone",
            PostgresBuiltInDataType.Time => "time without time zone",
            _ => builtIn.Type.CanonicalName(),
        };
    }

    private static (string Schema, SqlName Name) SplitSchema(QualifiedName qualifiedName)
    {
        var segments = qualifiedName.Segments.Select(i => i.Name).ToArray();

        return segments.Length switch
        {
            1 => ("public", SqlName.Object(segments[0])),
            2 => (segments[0], SqlName.Object(segments[1])),
            // A 3-part name (catalog.schema.object) would silently drop the catalog; reject
            // it rather than deploy to a different place than written.
            _ => throw new NotImplementedException(
                $"Catalog-qualified names ({string.Join('.', segments)}) are not supported; "
                + "use schema.object."),
        };
    }
}