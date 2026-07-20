using System.Text;
using Squill.Core;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

/// <summary>
/// Generates PostgreSQL DDL from schema deltas. This is pure model-to-SQL logic
/// with no database dependency, so it can be unit-tested without a live server.
/// </summary>
public class PostgresScriptGenerator
{
    /// <summary>
    /// Generates a single script covering every delta in the comparison, in order, with a
    /// blank line between steps so the generated (or previewed) script is easier to read.
    /// </summary>
    public string GenerateScript(SchemaComparison comparison)
    {
        var sb = new StringBuilder();

        foreach (var delta in comparison.Deltas)
        {
            sb.Append(GenerateScriptForDelta(delta));
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public string GenerateScriptForDelta(SchemaDelta delta)
    {
        if (delta is CreateDelta createDelta)
        {
            return GenerateCreateScript(createDelta);
        }

        if (delta is AlterDelta alterDelta)
        {
            return GenerateAlterScript(alterDelta);
        }

        if (delta is RebuildTableDelta rebuildDelta)
        {
            return GenerateRebuildScript(rebuildDelta);
        }

        if (delta is DropDelta dropDelta)
        {
            return GenerateDropScript(dropDelta);
        }

        throw new NotImplementedException();
    }

    // Emits a DROP statement for a standalone object no longer present in the source.
    private string GenerateDropScript(DropDelta dropDelta)
    {
        var element = dropDelta.Element;

        if (element.Name is not string name)
        {
            throw new ArgumentException("Cannot drop an object without a name");
        }

        var parsed = SqlName.Parse(name);

        return element.Type switch
        {
            // A table can be referenced by other objects (FKs, views); CASCADE removes
            // those dependencies along with it so the drop doesn't fail. Dropping a table
            // is destructive, which is why it is gated by DropObjectsNotInSource and, when
            // it holds data, blocked unless BlockOnPossibleDataLoss is disabled.
            PostgresElementTypes.SqlTable =>
                $"DROP TABLE {SchemaQualified(element, parsed)} CASCADE;{Environment.NewLine}",

            // An index lives in its table's schema; qualify it so a non-public index is
            // found. IF EXISTS keeps the drop idempotent if a prior step (e.g. dropping its
            // table) already removed it.
            PostgresElementTypes.SqlIndex =>
                $"DROP INDEX IF EXISTS {SchemaQualified(element, parsed)};{Environment.NewLine}",

            // An extension is not schema-scoped (globally named per database).
            PostgresElementTypes.SqlExtension =>
                $"DROP EXTENSION IF EXISTS {parsed.QuotedUnqualified};{Environment.NewLine}",

            // A schema is dropped after its objects; RESTRICT (the default) fails loudly if
            // anything still lives in it rather than silently cascading a drop. IF EXISTS
            // keeps it idempotent if the schema was already removed by an earlier step.
            PostgresElementTypes.SqlSchema =>
                $"DROP SCHEMA IF EXISTS {parsed.QuotedUnqualified};{Environment.NewLine}",

            _ => throw new NotImplementedException(
                $"Dropping an element of type {element.Type} is not supported."),
        };
    }

    // A schema-qualified quoted name for a schema-scoped object (table or index), using its
    // Schema relationship. Emitted unqualified for the default "public" schema to keep the
    // common case clean (public is on the search_path).
    private static string SchemaQualified(Element element, SqlName bareName)
    {
        var schema = GetSchema(element);

        return schema is null or "public"
            ? bareName.QuotedUnqualified
            : SqlName.Object(schema, bareName.UnqualifiedName).Sql;
    }

    // Emits ALTER TABLE ... ADD / DROP / ALTER COLUMN statements for an in-place table
    // alteration.
    private string GenerateAlterScript(AlterDelta alterDelta)
    {
        if (alterDelta.SourceElement.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }

        var quotedTableName = SqlName.Parse(tableName).Sql;

        var sb = new StringBuilder();

        foreach (var change in alterDelta.ColumnChanges)
        {
            var quotedColumn = $"\"{SqlName.UnqualifiedOf(change.ColumnName)}\"";

            switch (change.Kind)
            {
                case ColumnChangeKind.Add:
                    sb.Append("ALTER TABLE ").Append(quotedTableName)
                        .Append(" ADD COLUMN ")
                        .Append(RenderColumnDefinition(change.SourceColumn!))
                        .AppendLine(";");
                    break;

                case ColumnChangeKind.Drop:
                    sb.Append("ALTER TABLE ").Append(quotedTableName)
                        .Append(" DROP COLUMN ").Append(quotedColumn)
                        .AppendLine(";");
                    break;

                case ColumnChangeKind.Alter:
                    AppendAlterColumn(sb, quotedTableName, quotedColumn,
                        change.SourceColumn!, change.TargetColumn!);
                    break;

                default:
                    throw new NotImplementedException($"Unknown column change: {change.Kind}");
            }
        }

        return sb.ToString();
    }

    // Emits ALTER COLUMN clauses for only the facets — type and nullability — that
    // actually changed between the current and desired column. Postgres requires a
    // separate ALTER COLUMN clause for each facet, and rewriting the whole table with a
    // redundant TYPE clause is wasteful, so unchanged facets are skipped. Identity changes
    // are handled by a rebuild (see PostgresTableDiffAnalyzer), never reaching here.
    private void AppendAlterColumn(
        StringBuilder sb, string quotedTableName, string quotedColumn, Element source, Element target)
    {
        var sourceType = GetTypeStringForColumn(source);
        var targetType = GetTypeStringForColumn(target);

        if (!string.Equals(sourceType, targetType, StringComparison.OrdinalIgnoreCase))
        {
            sb.Append("ALTER TABLE ").Append(quotedTableName)
                .Append(" ALTER COLUMN ").Append(quotedColumn)
                .Append(" TYPE ").Append(sourceType)
                .AppendLine(";");
        }

        // IsNullable is only stored when the column is NOT NULL (nullable == false); an
        // absent property means nullable. Only emit SET/DROP NOT NULL when nullability
        // actually changed.
        var sourceNotNull = source.GetProperty<bool?>(PostgresPropertyNames.IsNullable) == false;
        var targetNotNull = target.GetProperty<bool?>(PostgresPropertyNames.IsNullable) == false;

        if (sourceNotNull != targetNotNull)
        {
            sb.Append("ALTER TABLE ").Append(quotedTableName)
                .Append(" ALTER COLUMN ").Append(quotedColumn)
                .Append(sourceNotNull ? " SET NOT NULL" : " DROP NOT NULL")
                .AppendLine(";");
        }
    }

    // Rebuilds a table that can't be altered in place: rename the existing table aside,
    // create the table with its desired shape, copy the data for the columns common to
    // both, then drop the renamed original. Wrapped in a transaction so a failure leaves
    // the original table untouched. This mirrors SSDT's table-rebuild data motion.
    private string GenerateRebuildScript(RebuildTableDelta rebuildDelta)
    {
        if (rebuildDelta.SourceElement.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }

        var sourceName = SqlName.Parse(tableName);
        var quotedTableName = sourceName.Sql;

        // A collision-resistant temporary name in the same schema for the old table.
        var oldName = sourceName.Sibling($"{sourceName.UnqualifiedName}__squill_rebuild_old");
        var quotedOldName = oldName.Sql;

        // Columns common to the desired table and the current one, in desired order — the
        // data that carries across the rebuild. Pair each with its old (target) definition
        // so a type change can be cast during the copy.
        var targetColumns = GetOrderedColumns(rebuildDelta.TargetElement)
            .ToDictionary(c => c.Name, c => c.Column);
        var carriedColumns = GetOrderedColumns(rebuildDelta.SourceElement)
            .Where(c => targetColumns.ContainsKey(c.Name))
            .ToList();

        // A GENERATED ALWAYS AS IDENTITY column rejects an explicit inserted value unless
        // the INSERT says OVERRIDING SYSTEM VALUE. Since a rebuild copies existing identity
        // values verbatim to preserve keys (and foreign-key references to them), emit that
        // clause when any carried column is an identity column.
        var identityColumns = carriedColumns
            .Where(c => c.Column.GetProperty<bool?>(PostgresPropertyNames.IsIdentity) == true)
            .ToList();

        var sb = new StringBuilder();

        sb.AppendLine("BEGIN;");
        sb.AppendLine();

        // Move the existing table aside so the new one can take its name.
        sb.Append("ALTER TABLE ").Append(quotedTableName)
            .Append(" RENAME TO ").Append(oldName.QuotedUnqualified).AppendLine(";");
        sb.AppendLine();

        // Renaming a table does not rename its constraints or indexes, so the old table
        // still owns names like "customer_pkey". Rename those aside too, otherwise the
        // recreated table can't reuse them and Postgres would auto-pick "customer_pkey1".
        // The current database's own dependent names are used (which may differ from the
        // desired model's), since those are what the renamed-aside table actually owns.
        AppendDependentRenames(sb, quotedOldName, rebuildDelta.TargetDependentElements);

        // Recreate the table with the desired shape and all its dependents.
        sb.Append(GenerateCreateTableScript(rebuildDelta.SourceElement, rebuildDelta.DependentElements));
        sb.AppendLine();

        // Copy the retained data across, casting any column whose type changed so the copy
        // into the new (differently typed) column succeeds.
        if (carriedColumns.Count > 0)
        {
            var insertList = string.Join(", ",
                carriedColumns.Select(c => $"\"{SqlName.UnqualifiedOf(c.Name)}\""));

            var selectList = string.Join(", ", carriedColumns.Select(c =>
            {
                var quoted = $"\"{SqlName.UnqualifiedOf(c.Name)}\"";
                var sourceType = GetTypeStringForColumn(c.Column);
                var targetType = GetTypeStringForColumn(targetColumns[c.Name]);

                // Only cast when the type actually changed, to keep the common case clean.
                return string.Equals(sourceType, targetType, StringComparison.OrdinalIgnoreCase)
                    ? quoted
                    : $"CAST({quoted} AS {sourceType})";
            }));

            sb.Append("INSERT INTO ").Append(quotedTableName)
                .Append(" (").Append(insertList).Append(')');

            if (identityColumns.Count > 0)
            {
                sb.Append(" OVERRIDING SYSTEM VALUE");
            }

            sb.AppendLine()
                .Append("SELECT ").Append(selectList)
                .Append(" FROM ").Append(quotedOldName).AppendLine(";");
            sb.AppendLine();
        }

        // Drop the renamed original.
        sb.Append("DROP TABLE ").Append(quotedOldName).AppendLine(";");
        sb.AppendLine();

        // Copying identity values verbatim (OVERRIDING SYSTEM VALUE) does not advance the
        // new column's identity sequence, which still starts at 1 — so the next generated
        // value would collide with a copied row. Advance each identity sequence past the
        // maximum copied value so future inserts get fresh keys.
        foreach (var (name, _) in identityColumns)
        {
            var quotedColumn = $"\"{SqlName.UnqualifiedOf(name)}\"";

            // setval(pg_get_serial_sequence(...), MAX(col)) leaves the sequence "already
            // used" so the next nextval() is MAX+1. COALESCE handles an empty table (no
            // rows copied): reset to 1 and mark it not-yet-called so the first value is 1.
            sb.Append("SELECT setval(pg_get_serial_sequence('")
                .Append(sourceName.ToString().Replace("'", "''")).Append("', '")
                .Append(SqlName.UnqualifiedOf(name).Replace("'", "''")).Append("'), ")
                .Append("COALESCE((SELECT MAX(").Append(quotedColumn).Append(") FROM ")
                .Append(quotedTableName).Append("), 1), ")
                .Append("(SELECT count(*) > 0 FROM ").Append(quotedTableName).AppendLine("));");
        }

        if (identityColumns.Count > 0)
        {
            sb.AppendLine();
        }

        sb.AppendLine("COMMIT;");

        return sb.ToString();
    }

    // Renames the renamed-aside old table's constraints and indexes out of the way so the
    // recreated table can reuse their canonical names. PK and FK constraints are renamed
    // with ALTER TABLE ... RENAME CONSTRAINT (which also renames a PK's backing index);
    // standalone indexes with ALTER INDEX ... RENAME TO.
    private static void AppendDependentRenames(
        StringBuilder sb, string quotedOldTableName, IList<Element> dependents)
    {
        var emitted = false;

        foreach (var dependent in dependents)
        {
            if (dependent.Name is not string name)
            {
                continue;
            }

            var parsed = SqlName.Parse(name);
            var asideName = $"{parsed.UnqualifiedName}__squill_rebuild_old";

            switch (dependent.Type)
            {
                case PostgresElementTypes.SqlPrimaryKeyConstraint:
                case PostgresElementTypes.SqlForeignKeyConstraint:
                    sb.Append("ALTER TABLE ").Append(quotedOldTableName)
                        .Append(" RENAME CONSTRAINT ").Append(parsed.QuotedUnqualified)
                        .Append(" TO \"").Append(asideName).AppendLine("\";");
                    emitted = true;
                    break;

                case PostgresElementTypes.SqlIndex:
                    // An index lives in the table's schema; qualify the rename with it.
                    var qualifiedIndex = parsed.Sql;
                    sb.Append("ALTER INDEX ").Append(qualifiedIndex)
                        .Append(" RENAME TO \"").Append(asideName).AppendLine("\";");
                    emitted = true;
                    break;
            }
        }

        if (emitted)
        {
            sb.AppendLine();
        }
    }

    // Renders a full column definition (name + type + identity/nullability), reusing the
    // exact rules the CREATE TABLE body uses, minus inline PRIMARY KEY (constraints are
    // handled separately when altering).
    private string RenderColumnDefinition(Element column)
    {
        if (column.Name is not string columnName)
        {
            throw new InvalidOperationException("Missing column name");
        }

        var columnType = GetTypeStringForColumn(column);
        var text = $"\"{SqlName.UnqualifiedOf(columnName)}\" {columnType}";

        var isIdentity = column.GetProperty<bool?>(PostgresPropertyNames.IsIdentity) == true;

        if (isIdentity)
        {
            var generation = column.GetProperty<string>(PostgresPropertyNames.IdentityGeneration);

            text += generation == "Always"
                ? " GENERATED ALWAYS AS IDENTITY"
                : " GENERATED BY DEFAULT AS IDENTITY";
        }
        else
        {
            var nullable = column.GetProperty<bool?>(PostgresPropertyNames.IsNullable);

            text += nullable == false ? " NOT NULL" : " NULL";
        }

        return text;
    }

    // The table's fully-quoted, schema-qualified SQL name (e.g. "staging"."film"). A table
    // in the default "public" schema is emitted unqualified ("film") to keep the common
    // case clean, since public is on the search_path.
    private static string QualifiedTableName(Element table)
    {
        if (table.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }

        var bare = SqlName.Parse(tableName);
        var schema = GetSchema(table);

        return schema is null or "public"
            ? bare.Sql
            : SqlName.Object(schema, bare.UnqualifiedName).Sql;
    }

    // The schema an element belongs to, from its Schema relationship, or null if it has
    // none. Shared with the diff via the model factory so both agree on schema identity.
    private static string? GetSchema(Element element) => PostgresModelFactory.GetSchema(element);

    // A foreign-key REFERENCES target, quoted and schema-qualified when the referenced
    // name carries a non-public schema segment, so the FK binds the right table regardless
    // of the session search_path. A bare or public name is emitted unqualified.
    private static string QualifiedForeignTable(string referencedName)
    {
        var parsed = SqlName.Parse(referencedName);

        // A single-segment or public-qualified name renders unqualified; a name qualified
        // with a non-public schema keeps its qualifier.
        return referencedName.Contains('.') && !referencedName.StartsWith("public.", StringComparison.Ordinal)
            ? parsed.Sql
            : parsed.QuotedUnqualified;
    }

    private static IEnumerable<string> GetOrderedColumnNames(Element table)
        => GetOrderedColumns(table).Select(c => c.Name);

    // The table's columns in declaration order, as (canonical name, element) pairs.
    private static IList<(string Name, Element Column)> GetOrderedColumns(Element table)
    {
        var columns = new List<(string, Element)>();

        foreach (var columnRelationship in table.Relationships
                     .Where(i => i.Name == PostgresRelationshipNames.Columns))
        {
            foreach (var column in columnRelationship.Entries.OfType<Element>()
                         .Where(i => i.Type == PostgresElementTypes.SqlSimpleColumn))
            {
                if (column.Name is string name)
                {
                    columns.Add((name, column));
                }
            }
        }

        return columns;
    }

    private string GenerateCreateScript(CreateDelta createDelta)
    {
        if (createDelta.Element.Type == PostgresElementTypes.SqlTable)
        {
            return GenerateCreateTableScript(createDelta.Element, createDelta.DependentElements);
        }

        if (createDelta.Element.Type == PostgresElementTypes.SqlExtension)
        {
            return GenerateCreateExtensionScript(createDelta.Element);
        }

        if (createDelta.Element.Type == PostgresElementTypes.SqlSchema)
        {
            return GenerateCreateSchemaScript(createDelta.Element);
        }

        throw new NotImplementedException();
    }

    private static string GenerateCreateSchemaScript(Element schema)
    {
        if (schema.Name is not string schemaName)
        {
            throw new ArgumentException("Schemas must have names");
        }

        // Squill models a schema as a declared object, so it is created explicitly. IF NOT
        // EXISTS keeps publish idempotent for a schema that may already be present.
        return $"CREATE SCHEMA IF NOT EXISTS {SqlName.Parse(schemaName).QuotedUnqualified};{Environment.NewLine}";
    }

    private string GenerateCreateTableScript(Element table, IList<Element> dependentElements)
    {
        if (table.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }

        // Stored names are canonical/unquoted; quote (and schema-qualify) when emitting SQL.
        var quotedTableName = QualifiedTableName(table);

        var sb = new StringBuilder();

        sb.Append("CREATE TABLE ").Append(quotedTableName).AppendLine("");
        sb.AppendLine("(");

        var columnText = new List<string>();

        var pk = dependentElements.SingleOrDefault(i => i.Type == PostgresElementTypes.SqlPrimaryKeyConstraint);

        var pkColumns = pk == null ? new List<string>() : GetPrimaryKeyColumns(pk);

        foreach (var columnRelationship in table.Relationships.Where(i => i.Name == PostgresRelationshipNames.Columns))
        {
            foreach (var column in columnRelationship.Entries.OfType<Element>().Where(i => i.Type == PostgresElementTypes.SqlSimpleColumn))
            {
                if (column.Name is not string columnName)
                {
                    throw new InvalidOperationException("Missing column name");
                }

                var columnType = GetTypeStringForColumn(column);

                // The stored column Name is table-qualified (e.g. film.title); a column
                // definition needs just the bare, quoted identifier.
                var text = $"\"{SqlName.UnqualifiedOf(columnName)}\" {columnType}";

                var isIdentity = column.GetProperty<bool?>(PostgresPropertyNames.IsIdentity) == true;
                var isSingleColumnPk = pkColumns.Count == 1 && pkColumns[0].Equals(columnName);

                if (isIdentity)
                {
                    // Identity columns are implicitly NOT NULL; Postgres rejects an
                    // explicit NULL and does not need a redundant NOT NULL, so no
                    // nullability suffix is emitted for identity columns.
                    var generation = column.GetProperty<string>(PostgresPropertyNames.IdentityGeneration);

                    text += generation == "Always"
                        ? " GENERATED ALWAYS AS IDENTITY"
                        : " GENERATED BY DEFAULT AS IDENTITY";
                }

                // A single-column PK is written inline; a composite PK is emitted as a
                // table-level clause below.
                if (isSingleColumnPk)
                {
                    // TODO: support named PK constraints
                    text += " PRIMARY KEY";
                }
                else if (!isIdentity)
                {
                    var nullable = column.GetProperty<bool?>(PostgresPropertyNames.IsNullable);

                    text += nullable == false ? " NOT NULL" : " NULL";
                }

                columnText.Add(text);
            }
        }

        if (pkColumns.Count > 1)
        {
            var pkColumnList = string.Join(", ", pkColumns.Select(c => $"\"{SqlName.UnqualifiedOf(c)}\""));

            // A composite PK is a table-level clause. Emit the constraint name so an
            // explicitly named PK (CONSTRAINT pk_x PRIMARY KEY (...)) keeps its name in
            // the database rather than getting the Postgres-generated <table>_pkey.
            columnText.Add($"CONSTRAINT {SqlName.Parse(pk!.Name!).QuotedUnqualified} PRIMARY KEY ({pkColumnList})");
        }

        foreach (var foreignKey in dependentElements.Where(i => i.Type == PostgresElementTypes.SqlForeignKeyConstraint))
        {
            columnText.Add(GetForeignKeyClause(foreignKey));
        }

        sb.Append("    ").AppendLine(string.Join($",{Environment.NewLine}    ", columnText));

        sb.AppendLine(");");

        foreach (var index in dependentElements.Where(i => i.Type == PostgresElementTypes.SqlIndex))
        {
            sb.AppendLine();
            sb.Append(GenerateCreateIndexScript(index, quotedTableName));
        }

        return sb.ToString();
    }

    private static string GenerateCreateIndexScript(Element index, string quotedTableName)
    {
        if (index.Name is not string indexName)
        {
            throw new ArgumentException("Indexes must have names");
        }

        // CREATE INDEX <name> ON <table>: the index name is bare (its schema is the
        // table's), so emit just the quoted final segment.
        var quotedIndexName = SqlName.Parse(indexName).QuotedUnqualified;

        var isUnique = index.GetProperty<bool?>(PostgresPropertyNames.IsUnique) == true;
        var indexMethod = index.GetProperty<string>(PostgresPropertyNames.IndexMethod);
        var filterPredicate = index.GetProperty<string>(PostgresPropertyNames.FilterPredicate);
        var storageParameters = index.GetProperty<string>(PostgresPropertyNames.StorageParameters);

        var columnSpecs = index.GetRelationship(PostgresRelationshipNames.ColumnSpecifications);

        if (columnSpecs == null)
        {
            throw new InvalidOperationException($"Index {indexName} has no column specifications");
        }

        var columnText = new List<string>();

        foreach (var columnSpec in columnSpecs.Entries.OfType<Element>()
                     .Where(i => i.Type == PostgresElementTypes.SqlIndexedColumnSpecification))
        {
            var columnReference = columnSpec.GetRelationship(PostgresRelationshipNames.Column)
                ?.Entries.OfType<Reference>().SingleOrDefault();

            if (columnReference == null)
            {
                throw new InvalidOperationException($"Index {indexName} column specification has no column reference");
            }

            // Column references are stored table-qualified (e.g. film.title); the
            // CREATE INDEX column list needs just the bare, quoted column name.
            var text = $"\"{SqlName.UnqualifiedOf(columnReference.Name)}\"";

            // Operator class (opclass) follows the column, before ASC/DESC — matching the
            // PostgreSQL CREATE INDEX synopsis. e.g. "embedding" vector_cosine_ops.
            if (columnSpec.GetProperty<string>(PostgresPropertyNames.OperatorClass) is { } operatorClass)
            {
                text += $" {operatorClass}";
            }

            var isAscending = columnSpec.GetProperty<bool?>(PostgresPropertyNames.IsAscending);

            if (isAscending == false)
            {
                text += " DESC";
            }

            // Postgres's default null ordering follows the sort direction: NULLS LAST for
            // ASC, NULLS FIRST for DESC. Only emit an explicit NULLS clause when it differs
            // from that default, so a model carrying the btree defaults (which both builders
            // now record) does not produce redundant NULLS LAST / NULLS FIRST in the DDL.
            if (columnSpec.GetProperty<bool?>(PostgresPropertyNames.NullsFirst) is bool nullsFirst)
            {
                var defaultNullsFirst = isAscending == false;

                if (nullsFirst != defaultNullsFirst)
                {
                    text += nullsFirst ? " NULLS FIRST" : " NULLS LAST";
                }
            }

            columnText.Add(text);
        }

        var sb = new StringBuilder();

        sb.Append("CREATE ");

        if (isUnique)
        {
            sb.Append("UNIQUE ");
        }

        sb.Append("INDEX ").Append(quotedIndexName).Append(" ON ").Append(quotedTableName);

        // btree is Postgres's default access method; emitting "USING btree" is redundant.
        // Both builders now record btree explicitly in the model, so suppress it here to
        // keep the generated DDL clean (a non-default method like hnsw is still emitted).
        if (indexMethod != null && !string.Equals(indexMethod, "btree", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" USING ").Append(indexMethod);
        }

        sb.Append(" (").Append(string.Join(", ", columnText)).Append(')');

        // WITH (...) storage parameters (e.g. HNSW's m / ef_construction). The stored
        // value is already the canonical "name=value, ..." list.
        if (storageParameters != null)
        {
            sb.Append(" WITH (").Append(storageParameters).Append(')');
        }

        // Partial (filtered) index: append the WHERE predicate carried in the model.
        if (filterPredicate != null)
        {
            sb.Append(" WHERE ").Append(filterPredicate);
        }

        sb.AppendLine(";");

        return sb.ToString();
    }

    private static string GenerateCreateExtensionScript(Element extension)
    {
        if (extension.Name is not string extensionName)
        {
            throw new ArgumentException("Extensions must have names");
        }

        // Extension names are stored unqualified; quote when emitting SQL. IF NOT EXISTS
        // keeps publish idempotent for extensions that may already be installed.
        var quotedName = SqlName.Parse(extensionName).QuotedUnqualified;

        var sb = new StringBuilder();

        sb.Append("CREATE EXTENSION IF NOT EXISTS ").Append(quotedName);

        var version = extension.GetProperty<string>(PostgresPropertyNames.Version);
        if (version != null)
        {
            sb.Append(" VERSION '").Append(version).Append('\'');
        }

        sb.AppendLine(";");

        return sb.ToString();
    }

    // CONSTRAINT "<name>" FOREIGN KEY ("a", "b") REFERENCES "table" ("x", "y")
    //   [ON DELETE <action>] [ON UPDATE <action>]
    private static string GetForeignKeyClause(Element foreignKey)
    {
        if (foreignKey.Name is not string fkName)
        {
            throw new ArgumentException("Foreign keys must have names");
        }

        var columns = GetReferenceColumnNames(foreignKey, PostgresRelationshipNames.ForeignKeyColumns);

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"Foreign key {fkName} has no referencing columns");
        }

        var foreignTableRef = foreignKey.GetRelationship(PostgresRelationshipNames.ForeignTable)
            ?.Entries.OfType<Reference>().SingleOrDefault();

        if (foreignTableRef == null)
        {
            throw new InvalidOperationException($"Foreign key {fkName} has no referenced table");
        }

        var foreignColumns = GetReferenceColumnNames(foreignKey, PostgresRelationshipNames.ForeignColumns);

        var sb = new StringBuilder();

        sb.Append("CONSTRAINT ").Append(SqlName.Parse(fkName).QuotedUnqualified)
            .Append(" FOREIGN KEY (")
            .Append(string.Join(", ", columns.Select(c => $"\"{c}\"")))
            .Append(") REFERENCES ")
            .Append(QualifiedForeignTable(foreignTableRef.Name));

        if (foreignColumns.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", foreignColumns.Select(c => $"\"{c}\""))).Append(')');
        }

        var deleteAction = foreignKey.GetProperty<string>(PostgresPropertyNames.DeleteAction);
        if (deleteAction != null)
        {
            sb.Append(" ON DELETE ").Append(RenderReferentialAction(deleteAction));
        }

        var updateAction = foreignKey.GetProperty<string>(PostgresPropertyNames.UpdateAction);
        if (updateAction != null)
        {
            sb.Append(" ON UPDATE ").Append(RenderReferentialAction(updateAction));
        }

        return sb.ToString();
    }

    // References store table-qualified names (e.g. orders.customer_id); a constraint
    // clause needs just the bare column identifiers, in order.
    private static IList<string> GetReferenceColumnNames(Element element, string relationshipName)
    {
        var relationship = element.GetRelationship(relationshipName);

        if (relationship == null)
        {
            return new List<string>();
        }

        return relationship.Entries
            .OfType<Reference>()
            .Select(r => SqlName.UnqualifiedOf(r.Name))
            .ToList();
    }

    private static string RenderReferentialAction(string action)
        => Enum.Parse<ReferentialAction>(action) switch
        {
            ReferentialAction.NoAction => "NO ACTION",
            ReferentialAction.Restrict => "RESTRICT",
            ReferentialAction.Cascade => "CASCADE",
            ReferentialAction.SetNull => "SET NULL",
            ReferentialAction.SetDefault => "SET DEFAULT",
            _ => throw new InvalidOperationException($"Unknown referential action: {action}"),
        };

    private static IList<string> GetPrimaryKeyColumns(Element pkConstraint)
    {
        var columnSpecs = pkConstraint.GetRelationship(PostgresRelationshipNames.ColumnSpecifications);

        if (columnSpecs == null)
        {
            return new List<string>();
        }

        var columns = new List<string>();

        foreach (var indexedColumn in columnSpecs.Entries.OfType<Element>()
                     .Where(i => i.Type == PostgresElementTypes.SqlIndexedColumnSpecification))
        {
            var column = indexedColumn
                .GetRelationship(PostgresRelationshipNames.Column)
                ?.Entries
                .OfType<Reference>()
                .SingleOrDefault();

            if (column == null)
            {
                throw new InvalidOperationException(
                    "Primary key column specification has no column reference");
            }

            columns.Add(column.Name);
        }

        return columns;
    }

    private string GetTypeStringForColumn(Element column)
    {
        // HACK.PI: assume there's a type specifier and built-in type reference
        var typeSpecifier = column.Relationships.Single(i => i.Name == PostgresRelationshipNames.TypeSpecifier);

        var typeElement = typeSpecifier.Entries
            .OfType<Element>()
            .Single(i => i.Type == PostgresElementTypes.SqlTypeSpecifier);

        var type = typeElement.Relationships.Single(i => i.Name == PostgresRelationshipNames.Type);

        var typeReference = type.Entries
            .OfType<Reference>()
            .Single();

        var maxLength = typeElement.GetProperty<int?>(PostgresPropertyNames.Length);
        var precision = typeElement.GetProperty<long?>(PostgresPropertyNames.Precision);
        var scale = typeElement.GetProperty<long?>(PostgresPropertyNames.Scale);

        return typeReference.Name.ToLower() switch
        {
            // A length-less character varying scripts as a bare `varchar`; Postgres has
            // no `varchar(MAX)` (that is SQL-Server syntax).
            "character varying" => maxLength != null ? $"varchar({maxLength})" : "varchar",
            // A custom type carrying a length modifier (e.g. pgvector's vector(3), where
            // Length holds the dimension) scripts with that modifier in parentheses.
            "vector" when maxLength != null => $"vector({maxLength})",
            // A `numeric(p, s)` column scripts with its precision and scale; a bare
            // `numeric` (no Precision property) stays unconstrained (issue #33).
            "numeric" when precision != null => $"numeric({precision}, {scale ?? 0})",
            _ => typeReference.Name,
        };
    }
}
