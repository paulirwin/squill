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

        foreach (var file in _workspace.Files.Where(i => i.Kind == FileKind.Compile))
        {
            await ProcessFile(file, model, cancellationToken);
        }

        return model;
    }

    private async Task ProcessFile(IFile file, Model model, CancellationToken cancellationToken)
    {
        var text = await file.ReadAllTextAsync(cancellationToken);

        var root = _postgresParser.Parse(text);

        foreach (var statement in root.Statements)
        {
            if (statement is CreateTableStatement createTableStatement)
            {
                foreach (var element in MakeCreateTableElements(createTableStatement))
                {
                    model.Elements.Add(element);
                }
            }
            else if (statement is CreateIndexStatement createIndexStatement)
            {
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
            else
            {
                throw new NotImplementedException(
                    $"Statement type {statement.GetType()} to Element transformation not yet implemented");
            }
        }
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

        AddTableColumnsRelationship(tableElement, tableName, createTableStatement, primaryKeyColumns, foreignKeys);

        var explicitPkName = CollectTableLevelConstraints(createTableStatement, tableName, primaryKeyColumns, foreignKeys);

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
        var referencedTable = ToSqlName(spec.ReferencedTable);

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

    private static void AddTableColumnsRelationship(Element sqlTableElement,
        SqlName tableName,
        CreateTableStatement createTableStatement,
        List<PostgresModelFactory.IndexedColumn> primaryKeyColumns,
        List<ForeignKeySpec> foreignKeys)
    {
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
            bool isIdentity = false;
            string? identityGeneration = null;

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

                    // PKs are not nullable
                    isNullable = false;
                }
                else if (constraint is DefaultColumnConstraint)
                {
                    // TODO: model column DEFAULT values
                }
                else if (constraint is IdentityColumnConstraint identity)
                {
                    isIdentity = true;
                    identityGeneration = identity.Always ? "Always" : "ByDefault";

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

            if (isIdentity)
            {
                element.Properties.Add(new Property(PostgresPropertyNames.IsIdentity, true));
                element.Properties.Add(new Property(PostgresPropertyNames.IdentityGeneration, identityGeneration!));
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

    // Splits a (possibly schema-qualified) table name into its schema and its bare,
    // schema-less name, mirroring how the DB builder stores tables: the element Name is
    // the bare table name (e.g. "film") and the schema is carried as a separate Schema
    // relationship. A CREATE TABLE with no schema qualifier defaults to "public", which
    // is where Postgres puts it — so both builders agree.
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