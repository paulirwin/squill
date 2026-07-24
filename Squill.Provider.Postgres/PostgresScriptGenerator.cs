using System.Text;
using Squill.Core;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

/// <summary>
/// Generates PostgreSQL DDL from schema deltas. This is pure model-to-SQL logic
/// with no database dependency, so it can be unit-tested without a live server.
/// </summary>
public class PostgresScriptGenerator : ScriptGeneratorBase
{
    // Adds a constraint that was held back from its table's CREATE to break a circular
    // foreign key dependency. By the time this runs, every table in the cycle exists.
    protected override string GenerateAddConstraintScript(AddConstraintDelta delta)
    {
        if (delta.Constraint.Type != PostgresElementTypes.SqlForeignKeyConstraint)
        {
            throw new NotImplementedException(
                $"Adding a constraint of type {delta.Constraint.Type} is not supported.");
        }

        if (delta.DefiningTable.Name is not string tableName)
        {
            throw new ArgumentException("Cannot add a constraint to a table without a name");
        }

        var quotedTable = SchemaQualified(delta.DefiningTable, SqlName.Parse(tableName));

        return $"ALTER TABLE {quotedTable} ADD {GetForeignKeyClause(delta.Constraint)};"
            + Environment.NewLine;
    }

    // Emits a DROP statement for a standalone object no longer present in the source.
    protected override string GenerateDropScript(DropDelta dropDelta)
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

            // A procedure is identified by its argument types as well as its name, so the
            // signature must be given for PostgreSQL to know which overload to drop.
            PostgresElementTypes.SqlProcedure =>
                $"DROP PROCEDURE IF EXISTS {RoutineSignature(element)};{Environment.NewLine}",

            // A function, like a procedure, is identified by its argument signature.
            PostgresElementTypes.SqlFunction =>
                $"DROP FUNCTION IF EXISTS {RoutineSignature(element)};{Environment.NewLine}",

            // An aggregate is likewise identified by its name and input-type signature.
            PostgresElementTypes.SqlAggregate =>
                $"DROP AGGREGATE IF EXISTS {RoutineSignature(element)};{Environment.NewLine}",

            // A view is dropped with RESTRICT (the default), so a view another object still
            // depends on fails loudly rather than silently cascading the drop.
            PostgresElementTypes.SqlView =>
                $"DROP VIEW IF EXISTS {SchemaQualified(element, parsed)};{Environment.NewLine}",

            // A user-defined type / domain is dropped with RESTRICT (the default) so a drop
            // fails loudly if a column still uses it, rather than silently cascading.
            PostgresElementTypes.SqlEnumType =>
                $"DROP TYPE IF EXISTS {SchemaQualified(element, parsed)};{Environment.NewLine}",

            PostgresElementTypes.SqlDomain =>
                $"DROP DOMAIN IF EXISTS {SchemaQualified(element, parsed)};{Environment.NewLine}",

            // A trigger is dropped by name qualified with the table it is on (a trigger's name
            // is unique per table, not per schema). IF EXISTS keeps it idempotent.
            PostgresElementTypes.SqlTrigger =>
                $"DROP TRIGGER IF EXISTS {TriggerName(element)} ON {TriggerTableQualified(element)};"
                + Environment.NewLine,

            _ => throw new NotImplementedException(
                $"Dropping an element of type {element.Type} is not supported."),
        };
    }

    // Renders a routine's (procedure or function) schema-qualified name followed by its
    // argument types, which is how PostgreSQL identifies one overload among several (e.g.
    // public."p"(integer,text)).
    private static string RoutineSignature(Element routine)
    {
        var routineName = routine.GetRequiredProperty<string>(PostgresPropertyNames.RoutineName);
        var argumentTypes = routine.GetRequiredProperty<string>(PostgresPropertyNames.ArgumentTypes);
        var schema = GetSchema(routine);

        var qualified = schema is null or "public"
            ? SqlName.Object(routineName).QuotedUnqualified
            : SqlName.Object(schema, routineName).Sql;

        return $"{qualified}({argumentTypes})";
    }

    // Emits a drop-and-recreate for an object whose definition changed but can't be altered
    // in place — an index. DROP INDEX IF EXISTS keeps it idempotent, then the new shape is
    // created. The DROP must precede the CREATE (same name, different definition).
    protected override string GenerateRecreateScript(RecreateDelta recreateDelta)
    {
        var source = recreateDelta.SourceElement;

        // A procedure is redefined in a single statement — no drop is needed, and issuing
        // one would momentarily remove a procedure that other sessions may be calling.
        // The signature is unchanged (it is part of the element's identity, so a changed
        // one would be a different element), which is what makes REPLACE valid here.
        if (source.Type == PostgresElementTypes.SqlProcedure)
        {
            return GenerateCreateProcedureScript(source);
        }

        // A function is likewise redefined in place with CREATE OR REPLACE FUNCTION; its
        // signature is part of its identity so a changed signature is a different element.
        if (source.Type == PostgresElementTypes.SqlFunction)
        {
            return GenerateCreateFunctionScript(source);
        }

        // A view whose column list changed cannot be replaced in place: PostgreSQL only
        // allows CREATE OR REPLACE VIEW to add trailing columns, and rejects a rename,
        // removal or reorder. Since a changed column list is what makes a view a changed
        // element at all, the safe form is always drop-then-create.
        if (source.Type == PostgresElementTypes.SqlView)
        {
            if (recreateDelta.TargetElement.Name is not string oldViewName)
            {
                throw new ArgumentException("Cannot drop a view without a name");
            }

            var view = new StringBuilder();

            view.Append("DROP VIEW IF EXISTS ")
                .Append(SchemaQualified(recreateDelta.TargetElement, SqlName.Parse(oldViewName)))
                .Append(';').AppendLine();

            view.Append(GenerateCreateViewScript(source));

            return view.ToString();
        }

        if (source.Type != PostgresElementTypes.SqlIndex)
        {
            throw new NotImplementedException(
                $"Recreating an element of type {source.Type} is not supported.");
        }

        if (recreateDelta.TargetElement.Name is not string oldName)
        {
            throw new ArgumentException("Cannot drop an index without a name");
        }

        var sb = new StringBuilder();

        // Drop the current index (qualified so a non-public index is found).
        sb.Append("DROP INDEX IF EXISTS ")
            .Append(SchemaQualified(recreateDelta.TargetElement, SqlName.Parse(oldName)))
            .Append(';').AppendLine();

        // Create the index with its desired shape, on its (qualified) table.
        sb.Append(GenerateCreateIndexScript(source, IndexTableName(source)));

        return sb.ToString();
    }

    // The qualified table name an index is defined on, from its IndexedObject reference.
    // The referenced table name is bare; an index shares its table's schema, so qualify
    // the table with the index's own schema (suppressed for public, as elsewhere).
    private static string IndexTableName(Element index)
    {
        var reference = index.GetRelationship(PostgresRelationshipNames.IndexedObject)
            ?.Entries.OfType<Reference>().SingleOrDefault();

        if (reference == null)
        {
            throw new InvalidOperationException(
                $"Index {index.Name} has no indexed-object reference");
        }

        var bare = SqlName.Parse(reference.Name);
        var schema = GetSchema(index);

        return schema is null or "public"
            ? bare.Sql
            : SqlName.Object(schema, bare.UnqualifiedName).Sql;
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
    protected override string GenerateAlterScript(AlterDelta alterDelta)
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

        // A default is a column facet: SET DEFAULT when the desired column has one (added
        // or changed), DROP DEFAULT when it no longer does. Only emit when it changed.
        var sourceDefault = source.GetProperty<string>(PostgresPropertyNames.DefaultValue);
        var targetDefault = target.GetProperty<string>(PostgresPropertyNames.DefaultValue);

        if (!string.Equals(sourceDefault, targetDefault, StringComparison.Ordinal))
        {
            sb.Append("ALTER TABLE ").Append(quotedTableName)
                .Append(" ALTER COLUMN ").Append(quotedColumn);

            sb.Append(sourceDefault is { } value
                    ? $" SET DEFAULT {PostgresDefaultValue.ToSql(value)}"
                    : " DROP DEFAULT")
                .AppendLine(";");
        }
    }

    protected override string ForeignKeyDropVerb => "DROP CONSTRAINT";

    protected override string QuoteForeignKeyDefiningTable(string referencedName) =>
        QualifiedForeignTable(referencedName);

    protected override string QuoteConstraintName(string constraintName) =>
        SqlName.Parse(constraintName).QuotedUnqualified;

    // A rename-aside name for a rebuilt object, guaranteed to stay within Postgres's 63-byte
    // identifier limit. See <see cref="ScriptGeneratorBase.ComputeRebuildAsideName"/> for the
    // truncate-and-hash logic. Static so the unit tests can call it without a generator instance.
    public static string RebuildAsideName(string baseName) =>
        ComputeRebuildAsideName(baseName, static s => Encoding.UTF8.GetByteCount(s), 63);

    // Rebuilds a table that can't be altered in place: rename the existing table aside,
    // create the table with its desired shape, copy the data for the columns common to
    // both, then drop the renamed original. Wrapped in a transaction so a failure leaves
    // the original table untouched. This mirrors SSDT's table-rebuild data motion.
    protected override string GenerateRebuildScript(RebuildTableDelta rebuildDelta)
    {
        if (rebuildDelta.SourceElement.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }

        var sourceName = SqlName.Parse(tableName);
        var quotedTableName = sourceName.Sql;

        // A collision-resistant temporary name in the same schema for the old table, kept
        // within Postgres's 63-byte identifier limit so it is not silently truncated.
        var oldName = sourceName.Sibling(RebuildAsideName(sourceName.UnqualifiedName));
        var quotedOldName = oldName.Sql;

        // Columns common to the desired table and the current one, in desired order — the
        // data that carries across the rebuild. Pair each with its old (target) definition
        // so a type change can be cast during the copy.
        var targetColumns = RelationshipHelpers.GetOrderedColumns(rebuildDelta.TargetElement)
            .ToDictionary(c => c.Name, c => c.Column);
        var carriedColumns = RelationshipHelpers.GetOrderedColumns(rebuildDelta.SourceElement)
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

        // Drop foreign keys on other tables that reference this one before renaming/dropping
        // it, otherwise DROP TABLE on the renamed-aside original fails. They are recreated
        // after the rebuild, still inside this transaction.
        AppendInboundForeignKeyDrops(sb, rebuildDelta.InboundForeignKeys);

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

        // Recreate the inbound FKs dropped above, now that the rebuilt table (with its key)
        // exists again. Their referenced key columns are preserved by the rebuild, so the
        // same definition is valid.
        if (rebuildDelta.InboundForeignKeys.Count > 0)
        {
            AppendInboundForeignKeyRecreates(sb, rebuildDelta.InboundForeignKeys);
            sb.AppendLine();
        }

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
            var asideName = RebuildAsideName(parsed.UnqualifiedName);

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

    // The " GENERATED ... AS IDENTITY" clause for an identity column, including the
    // parenthesized sequence-option list (issue #13) when any non-default option is
    // modeled — in canonical START/INCREMENT/MINVALUE/MAXVALUE/CACHE/CYCLE order.
    private static string IdentityClause(Element column)
    {
        var generation = column.GetProperty<string>(PostgresPropertyNames.IdentityGeneration);

        var text = generation == "Always"
            ? " GENERATED ALWAYS AS IDENTITY"
            : " GENERATED BY DEFAULT AS IDENTITY";

        var options = new List<string>();

        if (column.GetProperty<long?>(PostgresPropertyNames.StartValue) is { } startValue)
        {
            options.Add($"START WITH {startValue}");
        }

        if (column.GetProperty<long?>(PostgresPropertyNames.Increment) is { } increment)
        {
            options.Add($"INCREMENT BY {increment}");
        }

        if (column.GetProperty<long?>(PostgresPropertyNames.MinValue) is { } minValue)
        {
            options.Add($"MINVALUE {minValue}");
        }

        if (column.GetProperty<long?>(PostgresPropertyNames.MaxValue) is { } maxValue)
        {
            options.Add($"MAXVALUE {maxValue}");
        }

        if (column.GetProperty<long?>(PostgresPropertyNames.CacheSize) is { } cacheSize)
        {
            options.Add($"CACHE {cacheSize}");
        }

        if (column.GetProperty<bool?>(PostgresPropertyNames.IsCycling) == true)
        {
            options.Add("CYCLE");
        }

        if (options.Count > 0)
        {
            text += $" ({string.Join(" ", options)})";
        }

        return text;
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
            text += IdentityClause(column);
        }
        else
        {
            var nullable = column.GetProperty<bool?>(PostgresPropertyNames.IsNullable);

            text += nullable == false ? " NOT NULL" : " NULL";
        }

        text += DefaultClause(column);

        return text;
    }

    // The " DEFAULT <value>" clause for a column carrying a modeled default, or an empty
    // string if it has none. The stored value is already canonical SQL (a numeric,
    // true/false, or a single-quoted string).
    private static string DefaultClause(Element column)
        => column.GetProperty<string>(PostgresPropertyNames.DefaultValue) is { } value
            ? $" DEFAULT {PostgresDefaultValue.ToSql(value)}"
            : string.Empty;

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


    protected override string GenerateCreateScript(CreateDelta createDelta)
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

        if (createDelta.Element.Type == PostgresElementTypes.SqlProcedure)
        {
            return GenerateCreateProcedureScript(createDelta.Element);
        }

        if (createDelta.Element.Type == PostgresElementTypes.SqlFunction)
        {
            return GenerateCreateFunctionScript(createDelta.Element);
        }

        if (createDelta.Element.Type == PostgresElementTypes.SqlAggregate)
        {
            return GenerateCreateAggregateScript(createDelta.Element);
        }

        if (createDelta.Element.Type == PostgresElementTypes.SqlView)
        {
            return GenerateCreateViewScript(createDelta.Element);
        }

        if (createDelta.Element.Type == PostgresElementTypes.SqlEnumType)
        {
            return GenerateCreateEnumTypeScript(createDelta.Element);
        }

        if (createDelta.Element.Type == PostgresElementTypes.SqlDomain)
        {
            return GenerateCreateDomainScript(createDelta.Element);
        }

        if (createDelta.Element.Type == PostgresElementTypes.SqlTrigger)
        {
            return GenerateCreateTriggerScript(createDelta.Element);
        }

        throw new NotImplementedException();
    }

    // Scripts a view as CREATE OR REPLACE, naming its columns explicitly so the deployed
    // view exposes exactly the shape the model records. The column list is what makes
    // REPLACE safe here: PostgreSQL refuses to replace a view whose column list changed,
    // and a changed column list is a changed element, which the comparison turns into a
    // RecreateDelta (DROP then CREATE) rather than a replace.
    private static string GenerateCreateViewScript(Element view)
    {
        var definition = view.GetRequiredProperty<string>(PostgresPropertyNames.Definition);
        var schema = GetSchema(view);

        if (view.Name is not string name)
        {
            throw new ArgumentException("Cannot create a view without a name");
        }

        var parsed = SqlName.Parse(name);

        var qualified = schema is null or "public"
            ? parsed.QuotedUnqualified
            : parsed.Sql;

        var sb = new StringBuilder();

        sb.Append("CREATE OR REPLACE VIEW ").Append(qualified);

        var columns = ViewColumnNames(view).ToList();

        if (columns.Count > 0)
        {
            sb.Append(" (")
                .Append(string.Join(", ", columns.Select(i => SqlName.Object(i).QuotedUnqualified)))
                .Append(')');
        }

        sb.AppendLine(" AS").Append(definition).AppendLine(";");

        return sb.ToString();
    }

    // A view's column names, taken from the trailing segment of each column element's
    // (view-qualified) name.
    private static IEnumerable<string> ViewColumnNames(Element view)
        => view.GetRelationship(PostgresRelationshipNames.Columns)
            ?.Entries.OfType<Element>()
            .Select(i => i.Name is { } columnName
                ? SqlName.Parse(columnName).UnqualifiedName
                : throw new ArgumentException("A view column must have a name"))
           ?? [];

    private static string GenerateCreateProcedureScript(Element procedure)
    {
        var routineName = procedure.GetRequiredProperty<string>(PostgresPropertyNames.RoutineName);
        var arguments = procedure.GetRequiredProperty<string>(PostgresPropertyNames.Arguments);
        var language = procedure.GetRequiredProperty<string>(PostgresPropertyNames.Language);
        var body = procedure.GetRequiredProperty<string>(PostgresPropertyNames.Body);
        var schema = GetSchema(procedure);

        var qualified = schema is null or "public"
            ? SqlName.Object(routineName).QuotedUnqualified
            : SqlName.Object(schema, routineName).Sql;

        var sb = new StringBuilder();

        // OR REPLACE makes publish idempotent and is also how an existing procedure's body
        // is updated in place — PostgreSQL has no ALTER PROCEDURE for the body. Replacing
        // is only valid while the signature is unchanged; a changed signature is a
        // different procedure, which the comparison surfaces as a separate create/drop.
        sb.Append("CREATE OR REPLACE PROCEDURE ").Append(qualified)
            .Append('(').Append(arguments).AppendLine(")");
        sb.Append("    LANGUAGE ").Append(SqlName.Object(language).QuotedUnqualified);

        if (procedure.GetProperty<bool?>(PostgresPropertyNames.IsSecurityDefiner) == true)
        {
            sb.AppendLine().Append("    SECURITY DEFINER");
        }

        sb.AppendLine().Append("AS ").Append(DollarQuote(body)).AppendLine(";");

        return sb.ToString();
    }

    private static string GenerateCreateFunctionScript(Element function)
    {
        var routineName = function.GetRequiredProperty<string>(PostgresPropertyNames.RoutineName);
        var arguments = function.GetRequiredProperty<string>(PostgresPropertyNames.Arguments);
        var returnType = function.GetRequiredProperty<string>(PostgresPropertyNames.ReturnType);
        var language = function.GetRequiredProperty<string>(PostgresPropertyNames.Language);
        var body = function.GetRequiredProperty<string>(PostgresPropertyNames.Body);
        var schema = GetSchema(function);

        var qualified = schema is null or "public"
            ? SqlName.Object(routineName).QuotedUnqualified
            : SqlName.Object(schema, routineName).Sql;

        var returnsSet = function.GetProperty<bool?>(PostgresPropertyNames.ReturnsSet) == true;

        var sb = new StringBuilder();

        // OR REPLACE makes publish idempotent and updates an existing function's body in
        // place while the signature is unchanged (a changed signature is a different
        // function, surfaced as a separate create/drop).
        sb.Append("CREATE OR REPLACE FUNCTION ").Append(qualified)
            .Append('(').Append(arguments).AppendLine(")");
        sb.Append("    RETURNS ");
        if (returnsSet)
        {
            sb.Append("SETOF ");
        }
        sb.AppendLine(returnType);
        sb.Append("    LANGUAGE ").Append(SqlName.Object(language).QuotedUnqualified);

        // Volatility is stored only when it is not the VOLATILE default; strictness only
        // when STRICT. Both go on their own indented lines like SECURITY DEFINER.
        if (function.GetProperty<string>(PostgresPropertyNames.Volatility) is { } volatility)
        {
            sb.AppendLine().Append("    ").Append(volatility);
        }

        if (function.GetProperty<bool?>(PostgresPropertyNames.IsStrict) == true)
        {
            sb.AppendLine().Append("    STRICT");
        }

        if (function.GetProperty<bool?>(PostgresPropertyNames.IsSecurityDefiner) == true)
        {
            sb.AppendLine().Append("    SECURITY DEFINER");
        }

        sb.AppendLine().Append("AS ").Append(DollarQuote(body)).AppendLine(";");

        return sb.ToString();
    }

    // PostgreSQL has no CREATE OR REPLACE AGGREGATE, so an aggregate is always scripted as a
    // plain CREATE. Its input types come from the argument signature and the mandatory
    // SFUNC/STYPE items follow. The SFUNC is stored schema-qualified (schema.name); it is
    // emitted verbatim so it resolves to the same function the model recorded.
    private static string GenerateCreateAggregateScript(Element aggregate)
    {
        var routineName = aggregate.GetRequiredProperty<string>(PostgresPropertyNames.RoutineName);
        var argumentTypes = aggregate.GetRequiredProperty<string>(PostgresPropertyNames.ArgumentTypes);
        var stateFunction = aggregate.GetRequiredProperty<string>(PostgresPropertyNames.StateFunction);
        var stateType = aggregate.GetRequiredProperty<string>(PostgresPropertyNames.StateType);
        var schema = GetSchema(aggregate);

        var qualified = schema is null or "public"
            ? SqlName.Object(routineName).QuotedUnqualified
            : SqlName.Object(schema, routineName).Sql;

        var sb = new StringBuilder();

        sb.Append("CREATE AGGREGATE ").Append(qualified)
            .Append('(').Append(argumentTypes).AppendLine(") (");
        sb.Append("    SFUNC = ").Append(RenderQualifiedFunctionName(stateFunction)).AppendLine(",");
        sb.Append("    STYPE = ").AppendLine(stateType);
        sb.AppendLine(");");

        return sb.ToString();
    }

    // Emits CREATE TRIGGER for a trigger element (issue #83). The trigger's behavior facets
    // (timing/events/level) are stored canonically, so they are emitted verbatim; the function
    // is stored schema-qualified and its literal arguments are re-quoted as string constants,
    // which is how PostgreSQL records and reports them.
    private static string GenerateCreateTriggerScript(Element trigger)
    {
        var name = trigger.GetRequiredProperty<string>(PostgresPropertyNames.RoutineName);
        var timing = trigger.GetRequiredProperty<string>(PostgresPropertyNames.Timing);
        var events = trigger.GetRequiredProperty<string>(PostgresPropertyNames.Events);
        var level = trigger.GetRequiredProperty<string>(PostgresPropertyNames.Level);
        var function = trigger.GetRequiredProperty<string>(PostgresPropertyNames.TriggerFunction);
        var arguments = trigger.GetRequiredProperty<string>(PostgresPropertyNames.FunctionArguments);

        var sb = new StringBuilder();

        sb.Append("CREATE TRIGGER ").AppendLine(SqlName.Object(name).QuotedUnqualified);
        sb.Append("    ").Append(timing).Append(' ').Append(events)
            .Append(" ON ").AppendLine(TriggerTableQualified(trigger));
        sb.Append("    FOR EACH ").AppendLine(level);
        sb.Append("    EXECUTE FUNCTION ").Append(RenderQualifiedFunctionName(function))
            .Append('(').Append(RenderTriggerArguments(arguments)).AppendLine(");");

        return sb.ToString();
    }

    // The trigger name, quoted. Stored bare (its uniqueness is per-table), so no schema.
    private static string TriggerName(Element trigger)
        => SqlName.Object(trigger.GetRequiredProperty<string>(PostgresPropertyNames.RoutineName))
            .QuotedUnqualified;

    // The schema-qualified, quoted name of the table a trigger fires on.
    private static string TriggerTableQualified(Element trigger)
    {
        var reference = trigger.GetRelationship(PostgresRelationshipNames.TriggerTable)
            ?.Entries.OfType<Reference>().FirstOrDefault()
            ?? throw new InvalidOperationException("A trigger element has no table relationship");

        var schema = GetSchema(trigger);
        var bareTable = reference.Name;

        // The reference may already be schema-qualified (schema.table); take the last segment
        // as the table and prefer the explicit schema on the reference when present.
        var dot = bareTable.LastIndexOf('.');
        if (dot >= 0)
        {
            return SqlName.Object(bareTable[..dot], bareTable[(dot + 1)..]).Sql;
        }

        return schema is null or "public"
            ? SqlName.Object(bareTable).QuotedUnqualified
            : SqlName.Object(schema, bareTable).Sql;
    }

    // Re-quotes a trigger function's comma-joined literal arguments as SQL string constants,
    // the form PostgreSQL stores and reports. An empty string means no arguments.
    private static string RenderTriggerArguments(string arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return string.Empty;
        }

        return string.Join(", ", arguments
            .Split(", ")
            .Select(argument => $"'{argument.Replace("'", "''")}'"));
    }

    // Renders a possibly-schema-qualified function name (schema.name) for emission, quoting
    // each segment. A bare name is emitted quoted as-is.
    private static string RenderQualifiedFunctionName(string name)
    {
        var dot = name.IndexOf('.');

        return dot < 0
            ? SqlName.Object(name).QuotedUnqualified
            : SqlName.Object(name[..dot], name[(dot + 1)..]).Sql;
    }

    /// <summary>
    /// Wraps a routine body in dollar quotes, choosing a tag that does not occur in the
    /// body. Dollar quoting has no escape sequence, so a body containing the delimiter
    /// would otherwise terminate the string early and produce invalid SQL.
    /// </summary>
    private static string DollarQuote(string body)
    {
        var tag = string.Empty;

        while (body.Contains($"${tag}$", StringComparison.Ordinal))
        {
            tag = tag.Length == 0 ? "squill" : $"{tag}_";
        }

        return $"${tag}${body}${tag}$";
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

    // CREATE TYPE name AS ENUM ('a', 'b', ...) — issue #75. PostgreSQL has no IF NOT EXISTS
    // for CREATE TYPE, so the type is created plainly; ordering (via the dependency analyzer)
    // ensures it precedes the tables that use it.
    private static string GenerateCreateEnumTypeScript(Element enumType)
    {
        if (enumType.Name is not string name)
        {
            throw new ArgumentException("Enum types must have names");
        }

        var qualifiedName = SchemaQualified(enumType, SqlName.Parse(name));
        var labels = PostgresModelFactory.GetEnumLabels(enumType);
        var labelList = string.Join(", ", labels.Select(l => $"'{l.Replace("'", "''")}'"));

        return $"CREATE TYPE {qualifiedName} AS ENUM ({labelList});{Environment.NewLine}";
    }

    // CREATE DOMAIN name AS <base type> [CONSTRAINT ... CHECK (...)] — issue #75.
    private string GenerateCreateDomainScript(Element domain)
    {
        if (domain.Name is not string name)
        {
            throw new ArgumentException("Domains must have names");
        }

        var qualifiedName = SchemaQualified(domain, SqlName.Parse(name));

        // A domain carries its base type as a TypeSpecifier relationship, the same shape a
        // column uses, so the column type renderer applies directly.
        var baseType = GetTypeStringForColumn(domain);

        var sb = new StringBuilder();
        sb.Append("CREATE DOMAIN ").Append(qualifiedName).Append(" AS ").Append(baseType);

        var check = domain.GetProperty<string>(PostgresPropertyNames.CheckExpression);
        if (check is not null)
        {
            sb.Append(" CHECK (").Append(check).Append(')');
        }

        sb.Append(';').Append(Environment.NewLine);

        return sb.ToString();
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

        // A single-column PK is written inline (col ... PRIMARY KEY) only when it has the
        // default <table>_pkey name; a differently-named single-column PK is emitted as a
        // table-level CONSTRAINT clause below so its name survives (an inline PRIMARY KEY
        // has no place for a name). A composite PK is always table-level.
        var pkHasDefaultName = pk?.Name is string rawPkName
            && string.Equals(
                SqlName.Parse(rawPkName).UnqualifiedName,
                $"{SqlName.UnqualifiedOf(tableName)}_pkey",
                StringComparison.Ordinal);

        var pkIsInline = pkColumns.Count == 1 && pkHasDefaultName;

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
                var isSingleColumnPk = pkIsInline && pkColumns[0].Equals(columnName);

                if (isIdentity)
                {
                    // Identity columns are implicitly NOT NULL; Postgres rejects an
                    // explicit NULL and does not need a redundant NOT NULL, so no
                    // nullability suffix is emitted for identity columns.
                    text += IdentityClause(column);
                }

                // An unnamed single-column PK is written inline; a composite or named PK is
                // emitted as a table-level clause below.
                if (isSingleColumnPk)
                {
                    text += " PRIMARY KEY";
                }
                else if (!isIdentity)
                {
                    var nullable = column.GetProperty<bool?>(PostgresPropertyNames.IsNullable);

                    text += nullable == false ? " NOT NULL" : " NULL";
                }

                text += DefaultClause(column);

                columnText.Add(text);
            }
        }

        if (pkColumns.Count > 0 && !pkIsInline)
        {
            var pkColumnList = string.Join(", ", pkColumns.Select(c => $"\"{SqlName.UnqualifiedOf(c)}\""));

            // A composite PK, or a single-column PK with an explicit (non-default) name, is
            // a table-level clause. Emit the constraint name so an explicitly named PK
            // (CONSTRAINT pk_x PRIMARY KEY (...)) keeps its name in the database rather than
            // getting the Postgres-generated <table>_pkey.
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

    // Updates an installed extension to the source-pinned version.
    protected override string GenerateAlterExtensionScript(AlterExtensionVersionDelta delta)
    {
        if (delta.SourceElement.Name is not string extensionName)
        {
            throw new ArgumentException("Extensions must have names");
        }

        // Extension names are stored unqualified; quote when emitting SQL. Postgres has no
        // IF EXISTS on ALTER EXTENSION ... UPDATE, but the extension is known to exist (it
        // was matched in the target), so the update is safe.
        var quotedName = SqlName.Parse(extensionName).QuotedUnqualified;

        return $"ALTER EXTENSION {quotedName} UPDATE TO '{delta.TargetVersion}';{Environment.NewLine}";
    }

    // CONSTRAINT "<name>" FOREIGN KEY ("a", "b") REFERENCES "table" ("x", "y")
    //   [ON DELETE <action>] [ON UPDATE <action>]
    protected override string GetForeignKeyClause(Element foreignKey)
    {
        if (foreignKey.Name is not string fkName)
        {
            throw new ArgumentException("Foreign keys must have names");
        }

        var columns = RelationshipHelpers.GetReferenceColumnNames(foreignKey, PostgresRelationshipNames.ForeignKeyColumns);

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

        var foreignColumns = RelationshipHelpers.GetReferenceColumnNames(foreignKey, PostgresRelationshipNames.ForeignColumns);

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
            // A fixed-length `character(n)` scripts with its length; a bare `character`
            // is char(1). Without the length the column would be created as char(1) and
            // the round-trip would disagree with the source (issue #97).
            "character" => maxLength != null ? $"char({maxLength})" : "char",
            // A custom type carrying a length modifier (e.g. pgvector's vector(3), where
            // Length holds the dimension) scripts with that modifier in parentheses.
            "vector" when maxLength != null => $"vector({maxLength})",
            // A `numeric(p, s)` column scripts with its precision and scale; a bare
            // `numeric` (no Precision property) stays unconstrained (issue #33).
            "numeric" when precision != null => $"numeric({precision}, {scale ?? 0})",
            // Bit-string types script with their length in parentheses. A bare `bit`
            // is fixed-length bit(1) and `bit varying` without a length is unbounded, so
            // the length is emitted only when present (issue #97).
            // https://www.postgresql.org/docs/current/datatype-bit.html
            "bit" => maxLength != null ? $"bit({maxLength})" : "bit",
            "bit varying" => maxLength != null ? $"bit varying({maxLength})" : "bit varying",
            _ => typeReference.Name,
        };
    }
}
