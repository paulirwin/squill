using System.Text;
using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Generates MariaDB DDL from schema deltas. Pure model-to-SQL logic with no database
/// dependency, so it can be unit-tested without a live server. MariaDB quotes identifiers
/// with backticks, expresses an auto-numbered key column with <c>AUTO_INCREMENT</c>, and
/// has no schema or extension objects.
/// </summary>
public class MariaDbScriptGenerator
{
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
        return delta switch
        {
            CreateDelta create => GenerateCreateScript(create),
            AlterDelta alter => GenerateAlterScript(alter),
            RebuildTableDelta rebuild => GenerateRebuildScript(rebuild),
            DropDelta drop => GenerateDropScript(drop),
            RecreateDelta recreate => GenerateRecreateScript(recreate),
            AddConstraintDelta addConstraint => GenerateAddConstraintScript(addConstraint),
            _ => throw new NotImplementedException(
                $"Scripting a delta of type {delta.GetType().Name} is not supported."),
        };
    }

    // Adds a constraint that was held back from its table's CREATE to break a circular
    // foreign key dependency. By the time this runs, every table in the cycle exists.
    private static string GenerateAddConstraintScript(AddConstraintDelta delta)
    {
        if (delta.Constraint.Type != MariaDbElementTypes.SqlForeignKeyConstraint)
        {
            throw new NotImplementedException(
                $"Adding a constraint of type {delta.Constraint.Type} is not supported.");
        }

        if (delta.DefiningTable.Name is not string tableName)
        {
            throw new ArgumentException("Cannot add a constraint to a table without a name");
        }

        return $"ALTER TABLE {SqlName.Parse(tableName).Sql} ADD {GetForeignKeyClause(delta.Constraint)};"
            + Environment.NewLine;
    }

    // ---- CREATE ----

    private string GenerateCreateScript(CreateDelta createDelta)
    {
        if (createDelta.Element.Type == MariaDbElementTypes.SqlTable)
        {
            return GenerateCreateTableScript(createDelta.Element, createDelta.DependentElements);
        }

        if (createDelta.Element.Type == MariaDbElementTypes.SqlIndex)
        {
            return GenerateCreateIndexScript(createDelta.Element, IndexTableName(createDelta.Element));
        }

        throw new NotImplementedException(
            $"Creating an element of type {createDelta.Element.Type} is not supported.");
    }

    private string GenerateCreateTableScript(Element table, IList<Element> dependentElements)
    {
        if (table.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }

        var quotedTableName = SqlName.Parse(tableName).Sql;

        var sb = new StringBuilder();

        sb.Append("CREATE TABLE ").Append(quotedTableName).AppendLine();
        sb.AppendLine("(");

        var lines = new List<string>();

        var pk = dependentElements.SingleOrDefault(i => i.Type == MariaDbElementTypes.SqlPrimaryKeyConstraint);
        var pkColumns = pk == null ? new List<string>() : GetKeyColumns(pk);

        foreach (var column in GetOrderedColumns(table))
        {
            lines.Add(RenderColumnDefinition(column.Column));
        }

        if (pkColumns.Count > 0)
        {
            var pkColumnList = string.Join(", ", pkColumns.Select(c => $"`{SqlName.UnqualifiedOf(c)}`"));
            lines.Add($"PRIMARY KEY ({pkColumnList})");
        }

        // A unique index whose backing definition is a dependent element becomes a
        // UNIQUE KEY clause in the table body, so its name is preserved.
        foreach (var index in dependentElements.Where(i =>
                     i.Type == MariaDbElementTypes.SqlIndex
                     && i.GetProperty<bool?>(MariaDbPropertyNames.IsUnique) == true))
        {
            lines.Add(RenderUniqueKeyClause(index));
        }

        foreach (var foreignKey in dependentElements.Where(i => i.Type == MariaDbElementTypes.SqlForeignKeyConstraint))
        {
            lines.Add(GetForeignKeyClause(foreignKey));
        }

        sb.Append("    ").AppendLine(string.Join($",{Environment.NewLine}    ", lines));
        sb.AppendLine(");");

        // Non-unique standalone indexes are emitted as separate CREATE INDEX statements.
        foreach (var index in dependentElements.Where(i =>
                     i.Type == MariaDbElementTypes.SqlIndex
                     && i.GetProperty<bool?>(MariaDbPropertyNames.IsUnique) != true))
        {
            sb.AppendLine();
            sb.Append(GenerateCreateIndexScript(index, quotedTableName));
        }

        return sb.ToString();
    }

    private string RenderColumnDefinition(Element column)
    {
        if (column.Name is not string columnName)
        {
            throw new InvalidOperationException("Missing column name");
        }

        var columnType = GetTypeStringForColumn(column);
        var text = $"`{SqlName.UnqualifiedOf(columnName)}` {columnType}";

        var nullable = column.GetProperty<bool?>(MariaDbPropertyNames.IsNullable);
        text += nullable == false ? " NOT NULL" : " NULL";

        if (column.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement) == true)
        {
            text += " AUTO_INCREMENT";
        }

        text += DefaultClause(column);

        return text;
    }

    private static string DefaultClause(Element column)
        => column.GetProperty<string>(MariaDbPropertyNames.DefaultValue) is { } value
            ? $" DEFAULT {MariaDbDefaultValue.ToSql(value)}"
            : string.Empty;

    private static string RenderUniqueKeyClause(Element index)
    {
        if (index.Name is not string indexName)
        {
            throw new ArgumentException("Indexes must have names");
        }

        var columns = GetKeyColumns(index);
        var columnList = string.Join(", ", columns.Select(c => $"`{SqlName.UnqualifiedOf(c)}`"));

        return $"UNIQUE KEY `{SqlName.Parse(indexName).UnqualifiedName}` ({columnList})";
    }

    private static string GenerateCreateIndexScript(Element index, string quotedTableName)
    {
        if (index.Name is not string indexName)
        {
            throw new ArgumentException("Indexes must have names");
        }

        var quotedIndexName = SqlName.Parse(indexName).QuotedUnqualified;

        var isUnique = index.GetProperty<bool?>(MariaDbPropertyNames.IsUnique) == true;
        var indexMethod = index.GetProperty<string>(MariaDbPropertyNames.IndexMethod);

        var columnText = new List<string>();

        foreach (var columnSpec in GetColumnSpecifications(index))
        {
            var columnReference = columnSpec.GetRelationship(MariaDbRelationshipNames.Column)
                ?.Entries.OfType<Reference>().SingleOrDefault()
                ?? throw new InvalidOperationException($"Index {indexName} column specification has no column reference");

            var text = $"`{SqlName.UnqualifiedOf(columnReference.Name)}`";

            if (columnSpec.GetProperty<bool?>(MariaDbPropertyNames.IsAscending) == false)
            {
                text += " DESC";
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

        // BTREE is the implicit method; only emit USING for a non-default method (e.g. HASH).
        if (indexMethod != null && !string.Equals(indexMethod, "BTREE", StringComparison.OrdinalIgnoreCase))
        {
            sb.Append(" USING ").Append(indexMethod);
        }

        sb.Append(" (").Append(string.Join(", ", columnText)).Append(')');
        sb.AppendLine(";");

        return sb.ToString();
    }

    // ---- ALTER ----

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
            var quotedColumn = $"`{SqlName.UnqualifiedOf(change.ColumnName)}`";

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
                    // MariaDB changes a column in place with MODIFY COLUMN, which restates
                    // the full definition (type + nullability + default).
                    sb.Append("ALTER TABLE ").Append(quotedTableName)
                        .Append(" MODIFY COLUMN ")
                        .Append(RenderColumnDefinition(change.SourceColumn!))
                        .AppendLine(";");
                    break;

                default:
                    throw new NotImplementedException($"Unknown column change: {change.Kind}");
            }
        }

        return sb.ToString();
    }

    // ---- RECREATE (standalone index whose definition changed) ----

    private string GenerateRecreateScript(RecreateDelta recreateDelta)
    {
        var source = recreateDelta.SourceElement;

        if (source.Type != MariaDbElementTypes.SqlIndex)
        {
            throw new NotImplementedException(
                $"Recreating an element of type {source.Type} is not supported.");
        }

        if (recreateDelta.TargetElement.Name is not string oldName)
        {
            throw new ArgumentException("Cannot drop an index without a name");
        }

        var tableName = IndexTableName(source);

        var sb = new StringBuilder();

        // DROP INDEX name ON table; then recreate. MariaDB scopes an index name to its table.
        sb.Append("DROP INDEX ").Append(SqlName.Parse(oldName).QuotedUnqualified)
            .Append(" ON ").Append(tableName).AppendLine(";");

        sb.Append(GenerateCreateIndexScript(source, tableName));

        return sb.ToString();
    }

    // ---- DROP ----

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
            MariaDbElementTypes.SqlTable =>
                $"DROP TABLE {parsed.Sql};{Environment.NewLine}",

            MariaDbElementTypes.SqlIndex =>
                $"DROP INDEX {parsed.QuotedUnqualified} ON {IndexTableName(element)};{Environment.NewLine}",

            _ => throw new NotImplementedException(
                $"Dropping an element of type {element.Type} is not supported."),
        };
    }

    // ---- REBUILD ----

    // The suffix appended to rename an object aside during a rebuild. MariaDB identifiers
    // allow up to 64 characters.
    private const string RebuildAsideSuffix = "__squill_rebuild_old";
    private const int MaxIdentifierChars = 64;

    public static string RebuildAsideName(string baseName)
    {
        var candidate = baseName + RebuildAsideSuffix;

        if (candidate.Length <= MaxIdentifierChars)
        {
            return candidate;
        }

        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(baseName)))[..8];

        var reserved = 1 + hash.Length + RebuildAsideSuffix.Length;
        var keep = Math.Max(0, MaxIdentifierChars - reserved);
        var truncatedBase = baseName.Length > keep ? baseName[..keep] : baseName;

        return $"{truncatedBase}_{hash}{RebuildAsideSuffix}";
    }

    // Rebuilds a table that can't be altered in place: rename the existing table aside,
    // create the desired table, copy the shared columns, then drop the renamed original.
    // MariaDB DDL is not transactional (each statement auto-commits), so unlike the Postgres
    // provider this is not wrapped in BEGIN/COMMIT; a failure mid-rebuild leaves the renamed
    // original in place under its aside name for manual recovery.
    private string GenerateRebuildScript(RebuildTableDelta rebuildDelta)
    {
        if (rebuildDelta.SourceElement.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }

        var sourceName = SqlName.Parse(tableName);
        var quotedTableName = sourceName.Sql;

        var oldName = sourceName.Sibling(RebuildAsideName(sourceName.UnqualifiedName));
        var quotedOldName = oldName.Sql;

        var targetColumns = GetOrderedColumns(rebuildDelta.TargetElement)
            .ToDictionary(c => c.Name, c => c.Column);
        var carriedColumns = GetOrderedColumns(rebuildDelta.SourceElement)
            .Where(c => targetColumns.ContainsKey(c.Name))
            .ToList();

        var sb = new StringBuilder();

        // Drop inbound FKs from other tables before renaming/dropping this one.
        AppendInboundForeignKeyDrops(sb, rebuildDelta.InboundForeignKeys);

        // Move the existing table aside so the new one can take its name.
        sb.Append("ALTER TABLE ").Append(quotedTableName)
            .Append(" RENAME TO ").Append(quotedOldName).AppendLine(";");
        sb.AppendLine();

        // Recreate the table with the desired shape and its dependents.
        sb.Append(GenerateCreateTableScript(rebuildDelta.SourceElement, rebuildDelta.DependentElements));
        sb.AppendLine();

        // Copy the retained data across, casting nothing (MariaDB coerces on insert).
        if (carriedColumns.Count > 0)
        {
            var columnList = string.Join(", ",
                carriedColumns.Select(c => $"`{SqlName.UnqualifiedOf(c.Name)}`"));

            sb.Append("INSERT INTO ").Append(quotedTableName)
                .Append(" (").Append(columnList).Append(')').AppendLine()
                .Append("SELECT ").Append(columnList)
                .Append(" FROM ").Append(quotedOldName).AppendLine(";");
            sb.AppendLine();
        }

        // Drop the renamed original.
        sb.Append("DROP TABLE ").Append(quotedOldName).AppendLine(";");

        // Recreate the inbound FKs dropped above.
        if (rebuildDelta.InboundForeignKeys.Count > 0)
        {
            sb.AppendLine();
            AppendInboundForeignKeyRecreates(sb, rebuildDelta.InboundForeignKeys);
        }

        return sb.ToString();
    }

    private static void AppendInboundForeignKeyDrops(StringBuilder sb, IList<Element> inboundForeignKeys)
    {
        if (inboundForeignKeys.Count == 0)
        {
            return;
        }

        foreach (var fk in inboundForeignKeys)
        {
            var (definingTable, fkName) = InboundForeignKeyNames(fk);

            sb.Append("ALTER TABLE ").Append(definingTable)
                .Append(" DROP FOREIGN KEY ").Append(fkName).AppendLine(";");
        }

        sb.AppendLine();
    }

    private static void AppendInboundForeignKeyRecreates(StringBuilder sb, IList<Element> inboundForeignKeys)
    {
        foreach (var fk in inboundForeignKeys)
        {
            var (definingTable, _) = InboundForeignKeyNames(fk);

            sb.Append("ALTER TABLE ").Append(definingTable)
                .Append(" ADD ").Append(GetForeignKeyClause(fk)).AppendLine(";");
        }
    }

    private static (string DefiningTable, string ConstraintName) InboundForeignKeyNames(Element fk)
    {
        if (fk.Name is not string fkName)
        {
            throw new ArgumentException("Foreign keys must have names");
        }

        var definingTableRef = fk.GetRelationship(MariaDbRelationshipNames.DefiningTable)
            ?.Entries.OfType<Reference>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Foreign key {fkName} has no defining table");

        return (SqlName.Parse(definingTableRef.Name).Sql, SqlName.Parse(fkName).QuotedUnqualified);
    }

    // ---- Foreign keys ----

    // CONSTRAINT `name` FOREIGN KEY (`a`, `b`) REFERENCES `table` (`x`, `y`)
    //   [ON DELETE <action>] [ON UPDATE <action>]
    private static string GetForeignKeyClause(Element foreignKey)
    {
        if (foreignKey.Name is not string fkName)
        {
            throw new ArgumentException("Foreign keys must have names");
        }

        var columns = GetReferenceColumnNames(foreignKey, MariaDbRelationshipNames.ForeignKeyColumns);

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"Foreign key {fkName} has no referencing columns");
        }

        var foreignTableRef = foreignKey.GetRelationship(MariaDbRelationshipNames.ForeignTable)
            ?.Entries.OfType<Reference>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Foreign key {fkName} has no referenced table");

        var foreignColumns = GetReferenceColumnNames(foreignKey, MariaDbRelationshipNames.ForeignColumns);

        var sb = new StringBuilder();

        sb.Append("CONSTRAINT ").Append(SqlName.Parse(fkName).QuotedUnqualified)
            .Append(" FOREIGN KEY (")
            .Append(string.Join(", ", columns.Select(c => $"`{c}`")))
            .Append(") REFERENCES ")
            .Append(SqlName.Parse(foreignTableRef.Name).Sql);

        if (foreignColumns.Count > 0)
        {
            sb.Append(" (").Append(string.Join(", ", foreignColumns.Select(c => $"`{c}`"))).Append(')');
        }

        var deleteAction = foreignKey.GetProperty<string>(MariaDbPropertyNames.DeleteAction);
        if (deleteAction != null)
        {
            sb.Append(" ON DELETE ").Append(RenderReferentialAction(deleteAction));
        }

        var updateAction = foreignKey.GetProperty<string>(MariaDbPropertyNames.UpdateAction);
        if (updateAction != null)
        {
            sb.Append(" ON UPDATE ").Append(RenderReferentialAction(updateAction));
        }

        return sb.ToString();
    }

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
        => Enum.Parse<Squill.MariaDbParser.Syntax.ReferentialAction>(action) switch
        {
            Squill.MariaDbParser.Syntax.ReferentialAction.NoAction => "NO ACTION",
            Squill.MariaDbParser.Syntax.ReferentialAction.Restrict => "RESTRICT",
            Squill.MariaDbParser.Syntax.ReferentialAction.Cascade => "CASCADE",
            Squill.MariaDbParser.Syntax.ReferentialAction.SetNull => "SET NULL",
            Squill.MariaDbParser.Syntax.ReferentialAction.SetDefault => "SET DEFAULT",
            _ => throw new InvalidOperationException($"Unknown referential action: {action}"),
        };

    // ---- Shared helpers ----

    // The quoted table name an index is defined on, from its IndexedObject reference.
    private static string IndexTableName(Element index)
    {
        var reference = index.GetRelationship(MariaDbRelationshipNames.IndexedObject)
            ?.Entries.OfType<Reference>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Index {index.Name} has no indexed-object reference");

        return SqlName.Parse(reference.Name).Sql;
    }

    private static IEnumerable<Element> GetColumnSpecifications(Element indexOrKey)
    {
        var columnSpecs = indexOrKey.GetRelationship(MariaDbRelationshipNames.ColumnSpecifications)
            ?? throw new InvalidOperationException($"{indexOrKey.Name} has no column specifications");

        return columnSpecs.Entries.OfType<Element>()
            .Where(i => i.Type == MariaDbElementTypes.SqlIndexedColumnSpecification);
    }

    private static IList<string> GetKeyColumns(Element indexOrKey)
    {
        var columns = new List<string>();

        foreach (var spec in GetColumnSpecifications(indexOrKey))
        {
            var column = spec.GetRelationship(MariaDbRelationshipNames.Column)
                ?.Entries.OfType<Reference>().SingleOrDefault()
                ?? throw new InvalidOperationException("Key column specification has no column reference");

            columns.Add(column.Name);
        }

        return columns;
    }

    private static IList<(string Name, Element Column)> GetOrderedColumns(Element table)
    {
        var columns = new List<(string, Element)>();

        foreach (var columnRelationship in table.Relationships
                     .Where(i => i.Name == MariaDbRelationshipNames.Columns))
        {
            foreach (var column in columnRelationship.Entries.OfType<Element>()
                         .Where(i => i.Type == MariaDbElementTypes.SqlSimpleColumn))
            {
                if (column.Name is string name)
                {
                    columns.Add((name, column));
                }
            }
        }

        return columns;
    }

    private string GetTypeStringForColumn(Element column)
    {
        var typeSpecifier = column.Relationships.Single(i => i.Name == MariaDbRelationshipNames.TypeSpecifier);

        var typeElement = typeSpecifier.Entries
            .OfType<Element>()
            .Single(i => i.Type == MariaDbElementTypes.SqlTypeSpecifier);

        var type = typeElement.Relationships.Single(i => i.Name == MariaDbRelationshipNames.Type);
        var typeReference = type.Entries.OfType<Reference>().Single();

        var maxLength = typeElement.GetProperty<int?>(MariaDbPropertyNames.Length);
        var precision = typeElement.GetProperty<long?>(MariaDbPropertyNames.Precision);
        var scale = typeElement.GetProperty<long?>(MariaDbPropertyNames.Scale);
        var isUnsigned = typeElement.GetProperty<bool?>(MariaDbPropertyNames.IsUnsigned) == true;

        var typeName = typeReference.Name.ToLowerInvariant();

        var rendered = typeName switch
        {
            "varchar" or "char" when maxLength != null => $"{typeName}({maxLength})",
            "decimal" or "numeric" when precision != null => $"{typeName}({precision}, {scale ?? 0})",
            _ => typeName,
        };

        if (isUnsigned)
        {
            rendered += " unsigned";
        }

        return rendered;
    }
}
