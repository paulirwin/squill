using System.Text;
using Squill.Core;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Generates MariaDB DDL from schema deltas. Pure model-to-SQL logic with no database
/// dependency, so it can be unit-tested without a live server. MariaDB quotes identifiers
/// with backticks, expresses an auto-numbered key column with <c>AUTO_INCREMENT</c>, and
/// has no schema or extension objects.
/// </summary>
public class MariaDbScriptGenerator : ScriptGeneratorBase
{
    // Adds a constraint that was held back from its table's CREATE to break a circular
    // foreign key dependency. By the time this runs, every table in the cycle exists.
    protected override string GenerateAddConstraintScript(AddConstraintDelta delta)
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

    protected override string GenerateCreateScript(CreateDelta createDelta)
    {
        if (createDelta.Element.Type == MariaDbElementTypes.SqlTable)
        {
            return GenerateCreateTableScript(createDelta.Element, createDelta.DependentElements);
        }

        if (createDelta.Element.Type == MariaDbElementTypes.SqlIndex)
        {
            return GenerateCreateIndexScript(createDelta.Element, IndexTableName(createDelta.Element));
        }

        // A constraint added to a table that already exists: there is no CREATE TABLE to carry
        // the clause, so it is added with ALTER TABLE. This covers CHECK constraints
        // (issue #120) as well as primary and foreign keys (issue #157).
        //
        // The engine validates the new constraint against the rows already in the table, so a
        // duplicate in a new key's columns, or an orphan row a new foreign key forbids, fails
        // the deploy rather than being quietly accepted.
        if (ConstraintClause(createDelta.Element) is { } constraintClause)
        {
            return $"ALTER TABLE {ConstraintTableName(createDelta.Element)} "
                + $"ADD {constraintClause};{Environment.NewLine}";
        }

        if (createDelta.Element.Type == MariaDbElementTypes.SqlProcedure)
        {
            return GenerateCreateProcedureScript(createDelta.Element);
        }

        if (createDelta.Element.Type == MariaDbElementTypes.SqlFunction)
        {
            return GenerateCreateFunctionScript(createDelta.Element);
        }

        if (createDelta.Element.Type == MariaDbElementTypes.SqlView)
        {
            return GenerateCreateViewScript(createDelta.Element);
        }

        if (createDelta.Element.Type == MariaDbElementTypes.SqlTrigger)
        {
            return GenerateCreateTriggerScript(createDelta.Element);
        }

        if (createDelta.Element.Type == MariaDbElementTypes.SqlEvent)
        {
            return GenerateCreateEventScript(createDelta.Element);
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

        // Rendered from the column specifications rather than bare names, so a prefix length
        // survives into the key — inside a PRIMARY KEY it decides which rows the table accepts
        // as unique, so widening it changes the table's semantics (issue #161).
        var pkKeys = pk == null
            ? new List<string>()
            : RelationshipHelpers.GetColumnSpecifications(pk)
                .Select(spec => RenderIndexKey(spec, "PRIMARY"))
                .ToList();

        foreach (var column in RelationshipHelpers.GetOrderedColumns(table))
        {
            lines.Add(RenderColumnDefinition(column.Column));
        }

        if (pkKeys.Count > 0)
        {
            lines.Add($"PRIMARY KEY ({string.Join(", ", pkKeys)})");
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

        // CHECK constraints are emitted as named table-level clauses so the deployed
        // constraint keeps the name the model carries (issue #120).
        foreach (var check in dependentElements.Where(i => i.Type == MariaDbElementTypes.SqlCheckConstraint))
        {
            lines.Add(GetCheckConstraintClause(check));
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

        // A generated column's value is computed, so it takes no DEFAULT and no
        // AUTO_INCREMENT; the GENERATED ALWAYS AS clause replaces both, and its storage kind
        // (STORED or VIRTUAL) is written explicitly rather than relying on the engine
        // default (issue #120).
        if (column.GetProperty<string>(MariaDbPropertyNames.GeneratedExpression) is { } generated)
        {
            var storage = column.GetProperty<bool?>(MariaDbPropertyNames.IsStored) == true
                ? "STORED"
                : "VIRTUAL";

            // No nullability suffix is emitted. MySQL accepts `... STORED NOT NULL` but
            // MariaDB rejects it inside a CREATE TABLE (in either position), and one DACPAC
            // serves both engines — so a NOT NULL generated column has no portable spelling
            // and is rejected at build time instead (see the model builder). NULL is the
            // default and both engines reject writing it explicitly here.
            text += $" GENERATED ALWAYS AS ({generated}) {storage}";

            return text;
        }

        text += nullable == false ? " NOT NULL" : " NULL";

        if (column.GetProperty<bool?>(MariaDbPropertyNames.IsAutoIncrement) == true)
        {
            text += " AUTO_INCREMENT";
        }

        text += DefaultClause(column);

        return text;
    }

    private static string DefaultClause(Element column)
    {
        var text = column.GetProperty<string>(MariaDbPropertyNames.DefaultValue) is { } value
            ? $" DEFAULT {MariaDbDefaultValue.ToSql(value)}"
            : string.Empty;

        // ON UPDATE CURRENT_TIMESTAMP follows the DEFAULT clause (issue #124). The stored token
        // carries any fractional-seconds precision (issue #144) and is already valid DDL.
        if (column.GetProperty<string>(MariaDbPropertyNames.OnUpdateCurrentTimestamp) is { } onUpdate)
        {
            text += $" ON UPDATE {onUpdate}";
        }

        return text;
    }

    private static string RenderUniqueKeyClause(Element index)
    {
        if (index.Name is not string indexName)
        {
            throw new ArgumentException("Indexes must have names");
        }

        var columnList = string.Join(", ", RelationshipHelpers.GetColumnSpecifications(index)
            .Select(spec => RenderIndexKey(spec, indexName)));

        return $"UNIQUE KEY `{SqlName.Parse(indexName).UnqualifiedName}` ({columnList})";
    }

    /// <summary>
    /// One index key as it is written inside a key list: a quoted column with any prefix length
    /// and sort direction, or — for a functional key — the expression in place of the column
    /// (issue #161).
    ///
    /// <para>
    /// The prefix length is what makes indexing a TEXT/BLOB column legal on MySQL at all, so
    /// omitting it here is not a cosmetic loss: the generated DDL is rejected outright with
    /// error 1170, and accepted by MariaDB with a silently substituted 768-byte prefix.
    /// </para>
    /// </summary>
    private static string RenderIndexKey(Element columnSpec, string ownerName)
    {
        string text;

        if (columnSpec.GetProperty<string>(MariaDbPropertyNames.KeyExpression) is { } keyExpression)
        {
            // MySQL requires a functional key to be parenthesized. The raw text may arrive
            // either way — a source key is stored as the text inside its parentheses (`a + b`),
            // while one read back from STATISTICS.EXPRESSION already carries them
            // (``(`a` + `b`)``) — so wrap only when it is not already wrapped, rather than
            // emitting a double-parenthesized key when scripting from an extracted model.
            var trimmed = keyExpression.Trim();

            text = IsWrappedInParentheses(trimmed) ? trimmed : $"({trimmed})";
        }
        else
        {
            var columnReference = columnSpec.GetRelationship(MariaDbRelationshipNames.Column)
                ?.Entries.OfType<Reference>().SingleOrDefault()
                ?? throw new InvalidOperationException(
                    $"{ownerName} column specification has no column reference");

            text = $"`{SqlName.UnqualifiedOf(columnReference.Name)}`";

            if (columnSpec.GetProperty<int?>(MariaDbPropertyNames.PrefixLength) is int prefixLength)
            {
                text += $"({prefixLength})";
            }
        }

        if (columnSpec.GetProperty<bool?>(MariaDbPropertyNames.IsAscending) == false)
        {
            text += " DESC";
        }

        return text;
    }

    /// <summary>
    /// Whether the whole expression is enclosed by one matching pair of parentheses, so wrapping
    /// it again would only add noise. The depth walk is what tells <c>(a + b)</c> — genuinely
    /// wrapped — from <c>(a) + (b)</c>, where the outer characters are parentheses but close
    /// each other mid-expression rather than spanning it.
    /// </summary>
    private static bool IsWrappedInParentheses(string expression)
    {
        if (expression.Length < 2 || expression[0] != '(' || expression[^1] != ')')
        {
            return false;
        }

        var depth = 0;

        for (var i = 0; i < expression.Length; i++)
        {
            depth += expression[i] switch { '(' => 1, ')' => -1, _ => 0 };

            // Back to zero before the end means the opening paren closed early.
            if (depth == 0 && i < expression.Length - 1)
            {
                return false;
            }
        }

        return depth == 0;
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

        foreach (var columnSpec in RelationshipHelpers.GetColumnSpecifications(index))
        {
            columnText.Add(RenderIndexKey(columnSpec, indexName));
        }

        var sb = new StringBuilder();

        sb.Append("CREATE ");

        if (isUnique)
        {
            sb.Append("UNIQUE ");
        }

        // FULLTEXT/SPATIAL are written as a prefix keyword, not as a USING method: both engines
        // reject `USING FULLTEXT` as a syntax error (issue #146).
        if (index.GetProperty<string>(MariaDbPropertyNames.IndexKind) is { } indexKind)
        {
            sb.Append(indexKind).Append(' ');
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

    protected override string GenerateRecreateScript(RecreateDelta recreateDelta)
    {
        var source = recreateDelta.SourceElement;

        // A procedure whose definition changed is dropped and recreated. Unlike PostgreSQL
        // there is no portable in-place redefinition: MySQL has no CREATE OR REPLACE
        // PROCEDURE (it is MariaDB-only) and neither engine can ALTER a routine's body or
        // parameters. DROP ... IF EXISTS keeps the pair idempotent.
        if (source.Type == MariaDbElementTypes.SqlProcedure)
        {
            return DropProcedureStatement(source) + GenerateCreateProcedureScript(source);
        }

        // A function whose definition changed is dropped and recreated, for the same reason
        // as a procedure — no portable in-place redefinition exists on both engines.
        if (source.Type == MariaDbElementTypes.SqlFunction)
        {
            return DropFunctionStatement(source) + GenerateCreateFunctionScript(source);
        }

        // A view whose column list changed is dropped and recreated. CREATE OR REPLACE VIEW
        // is MariaDB-only (MySQL has no such form), so the portable spelling for both
        // engines is DROP ... IF EXISTS followed by CREATE.
        if (source.Type == MariaDbElementTypes.SqlView)
        {
            return DropViewStatement(source) + GenerateCreateViewScript(source);
        }

        // A trigger whose definition changed is dropped and recreated. Neither engine can
        // ALTER a trigger, and CREATE OR REPLACE TRIGGER is MariaDB-only, so the portable
        // spelling is DROP ... IF EXISTS followed by CREATE.
        if (source.Type == MariaDbElementTypes.SqlTrigger)
        {
            return DropTriggerStatement(source) + GenerateCreateTriggerScript(source);
        }

        // An event whose schedule or body changed is likewise dropped and recreated. MariaDB
        // does have ALTER EVENT, but its clauses must appear in a fixed order and MySQL's
        // partial-update semantics differ, so DROP + CREATE is the portable spelling and it
        // restates the whole definition rather than diffing it facet by facet.
        if (source.Type == MariaDbElementTypes.SqlEvent)
        {
            return DropEventStatement(source) + GenerateCreateEventScript(source);
        }

        // A constraint redefined under the same name: a CHECK whose predicate changed
        // (issue #156), or a primary or foreign key whose columns or referential actions
        // changed (issue #157). Neither engine can alter any of them in place, so each is
        // dropped and re-added.
        //
        // Dropping first is required rather than merely tidy for a primary key — a table may
        // only have one, so adding the new one before removing the old would be rejected — and
        // it is what makes the re-add validate against the existing rows, so a tightened
        // constraint the data violates fails the deploy instead of silently leaving the old one
        // in force.
        if (ConstraintClause(source) is { } constraintClause)
        {
            var drop = DropConstraintStatement(recreateDelta.TargetElement)
                ?? throw new ArgumentException(
                    $"Cannot drop a {source.Type} to recreate it: it has no name.");

            return drop
                + $"ALTER TABLE {ConstraintTableName(source)} ADD {constraintClause};"
                + Environment.NewLine;
        }

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

    protected override string GenerateDropScript(DropDelta dropDelta)
    {
        var element = dropDelta.Element;

        // A constraint belongs to its table and is dropped through it. Each kind has its own
        // spelling on these engines, so the statement comes from DropConstraintStatement — and
        // a primary key is handled before the name check below, since it has no usable name of
        // its own (issue #157).
        if (DropConstraintStatement(element) is { } dropConstraint)
        {
            return dropConstraint;
        }

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

            // Neither engine allows overloading, so the name alone identifies the procedure
            // — no argument signature is needed, unlike PostgreSQL.
            MariaDbElementTypes.SqlProcedure => DropProcedureStatement(element),

            MariaDbElementTypes.SqlFunction => DropFunctionStatement(element),

            MariaDbElementTypes.SqlView => DropViewStatement(element),

            MariaDbElementTypes.SqlTrigger => DropTriggerStatement(element),

            MariaDbElementTypes.SqlEvent => DropEventStatement(element),

            _ => throw new NotImplementedException(
                $"Dropping an element of type {element.Type} is not supported."),
        };
    }

    // Scripts a view, naming its columns explicitly so the deployed view exposes exactly
    // the shape the model records. CREATE OR REPLACE is not used: it is MariaDB-only syntax
    // and this generator targets MySQL too, so a changed view is scripted as DROP + CREATE.
    private static string GenerateCreateViewScript(Element view)
    {
        var definition = view.GetRequiredProperty<string>(MariaDbPropertyNames.Definition);

        if (view.Name is not string name)
        {
            throw new ArgumentException("Cannot create a view without a name");
        }

        var sb = new StringBuilder();

        sb.Append("CREATE VIEW ").Append(SqlName.Parse(name).Sql);

        var columns = ViewColumnNames(view).ToList();

        if (columns.Count > 0)
        {
            sb.Append(" (")
                .Append(string.Join(", ", columns.Select(i => SqlName.Object(i).Sql)))
                .Append(')');
        }

        sb.AppendLine(" AS").Append(definition).AppendLine(";");

        return sb.ToString();
    }

    // A view's column names, taken from the trailing segment of each column element's
    // (view-qualified) name.
    private static IEnumerable<string> ViewColumnNames(Element view)
        => view.GetRelationship(MariaDbRelationshipNames.Columns)
            ?.Entries.OfType<Element>()
            .Select(i => i.Name is { } columnName
                ? SqlName.Parse(columnName).UnqualifiedName
                : throw new ArgumentException("A view column must have a name"))
           ?? [];

    private static string DropViewStatement(Element view)
    {
        if (view.Name is not string name)
        {
            throw new ArgumentException("Cannot drop a view without a name");
        }

        return $"DROP VIEW IF EXISTS {SqlName.Parse(name).Sql};{Environment.NewLine}";
    }

    // Scripts a trigger: CREATE TRIGGER `name` {BEFORE|AFTER} {INSERT|UPDATE|DELETE}
    // ON `table` FOR EACH ROW <body>. The body — a BEGIN ... END block or a single statement —
    // is emitted verbatim, exactly as ACTION_STATEMENT reports it. CREATE OR REPLACE is not
    // used: it is MariaDB-only syntax and this generator targets MySQL too, so a changed
    // trigger is scripted as DROP + CREATE.
    private static string GenerateCreateTriggerScript(Element trigger)
    {
        var triggerName = trigger.GetRequiredProperty<string>(MariaDbPropertyNames.RoutineName);
        var timing = trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Timing);
        var @event = trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Event);
        var body = trigger.GetRequiredProperty<string>(MariaDbPropertyNames.Body);

        var sb = new StringBuilder();

        sb.Append("CREATE TRIGGER ").Append(SqlName.Object(triggerName).Sql)
            .Append(' ').Append(timing).Append(' ').Append(@event)
            .Append(" ON ").Append(TriggerTableName(trigger))
            .Append(" FOR EACH ROW").AppendLine();

        // The body is emitted verbatim and the statement is not terminated with a semicolon:
        // a BEGIN ... END body contains its own, and each delta is sent to the server as a
        // single command, so no DELIMITER handling is needed.
        sb.AppendLine(body);

        return sb.ToString();
    }

    // Scripts a scheduled event: CREATE EVENT `name` ON SCHEDULE <schedule> [ON COMPLETION
    // PRESERVE] [ENABLE|DISABLE|DISABLE ON SLAVE] [COMMENT '...'] DO <body>. The clause order
    // is the one both engines' syntax requires. Facets equal to their default are absent from
    // the model, so they are simply not emitted.
    private static string GenerateCreateEventScript(Element element)
    {
        var name = SqlName.UnqualifiedOf((string)element.Name!);
        var body = element.GetRequiredProperty<string>(MariaDbPropertyNames.Body);

        var sb = new StringBuilder();

        sb.Append("CREATE EVENT ").Append(SqlName.Object(name).Sql)
            .Append(" ON SCHEDULE ").Append(EventScheduleClause(element));

        // GetProperty<bool> would unbox a null Value, so the flag is read by presence: it is
        // stored only when true (NOT PRESERVE is the default and is never recorded).
        if (element.Properties.Any(i => i.Name == MariaDbPropertyNames.PreserveOnCompletion))
        {
            sb.Append(" ON COMPLETION PRESERVE");
        }

        // The catalog's status spellings map back onto the DDL clauses that produce them.
        // ENABLED is the default and is never stored, so it never needs emitting.
        if (element.GetProperty<string>(MariaDbPropertyNames.Status) is { } status)
        {
            sb.Append(status switch
            {
                "DISABLED" => " DISABLE",
                "SLAVESIDE_DISABLED" => " DISABLE ON SLAVE",
                _ => throw new NotSupportedException(
                    $"Event status '{status}' is not supported."),
            });
        }

        if (element.GetProperty<string>(MariaDbPropertyNames.Comment) is { } comment)
        {
            sb.Append(" COMMENT ").Append(QuoteLiteral(comment));
        }

        sb.Append(" DO").AppendLine();

        // The body is emitted verbatim and the statement is not terminated with a semicolon:
        // a BEGIN ... END body contains its own, and each delta is sent to the server as a
        // single command, so no DELIMITER handling is needed.
        sb.AppendLine(body);

        return sb.ToString();
    }

    // The ON SCHEDULE clause. A ONE TIME event runs AT a fixed timestamp; a RECURRING one runs
    // EVERY interval, with the STARTS the model always carries for it (a recurring event
    // without one is rejected at build time, since the server would synthesize a start from
    // the deploy clock and the event could never match its declaration again).
    private static string EventScheduleClause(Element element)
    {
        var eventType = element.GetRequiredProperty<string>(MariaDbPropertyNames.EventType);

        if (eventType == "ONE TIME")
        {
            var executeAt = element.GetRequiredProperty<string>(MariaDbPropertyNames.ExecuteAt);

            return $"AT {QuoteLiteral(executeAt)}";
        }

        var intervalValue = element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalValue);
        var intervalField = element.GetRequiredProperty<string>(MariaDbPropertyNames.IntervalField);
        var starts = element.GetRequiredProperty<string>(MariaDbPropertyNames.Starts);

        var sb = new StringBuilder();

        sb.Append("EVERY ").Append(RenderIntervalValue(intervalValue))
            .Append(' ').Append(intervalField)
            .Append(" STARTS ").Append(QuoteLiteral(starts));

        if (element.GetProperty<string>(MariaDbPropertyNames.Ends) is { } ends)
        {
            sb.Append(" ENDS ").Append(QuoteLiteral(ends));
        }

        return sb.ToString();
    }

    // A simple count is written bare (EVERY 1 DAY). A compound interval is stored the way the
    // catalog reports it — space-separated, e.g. "2 3" for DAY_HOUR — but the CREATE syntax
    // accepts only the quoted, colon-separated literal, so it is converted back here.
    private static string RenderIntervalValue(string intervalValue)
        => intervalValue.Contains(' ')
            ? QuoteLiteral(intervalValue.Replace(' ', ':'))
            : intervalValue;

    // A single-quoted SQL string literal, with embedded quotes doubled.
    private static string QuoteLiteral(string value)
        => $"'{value.Replace("'", "''")}'";

    private static string DropEventStatement(Element element)
    {
        var name = SqlName.UnqualifiedOf((string)element.Name!);

        return $"DROP EVENT IF EXISTS {SqlName.Object(name).Sql};{Environment.NewLine}";
    }

    private static string DropTriggerStatement(Element trigger)
    {
        var triggerName = trigger.GetRequiredProperty<string>(MariaDbPropertyNames.RoutineName);

        // A trigger name is unique within the database, so DROP TRIGGER names it alone — no
        // table qualifier, which the syntax does not accept.
        return $"DROP TRIGGER IF EXISTS {SqlName.Object(triggerName).Sql};{Environment.NewLine}";
    }

    // The quoted table name a trigger fires on, from its TriggerTable reference.
    private static string TriggerTableName(Element trigger)
    {
        var reference = trigger.GetRelationship(MariaDbRelationshipNames.TriggerTable)
            ?.Entries.OfType<Reference>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Trigger {trigger.Name} has no trigger-table reference");

        return SqlName.Parse(reference.Name).Sql;
    }

    private static string DropProcedureStatement(Element procedure)
    {
        if (procedure.Name is not string name)
        {
            throw new ArgumentException("Cannot drop a procedure without a name");
        }

        return $"DROP PROCEDURE IF EXISTS {SqlName.Parse(name).Sql};{Environment.NewLine}";
    }

    private static string GenerateCreateProcedureScript(Element procedure)
    {
        if (procedure.Name is not string name)
        {
            throw new ArgumentException("Cannot create a procedure without a name");
        }

        var arguments = procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments);
        var body = procedure.GetRequiredProperty<string>(MariaDbPropertyNames.Body);

        var sb = new StringBuilder();

        sb.Append("CREATE PROCEDURE ").Append(SqlName.Parse(name).Sql)
            .Append('(').Append(arguments).AppendLine(")");

        // Only non-default characteristics are stored on the element, so each is written
        // only when present — matching what the engines report for an unadorned procedure.
        if (procedure.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic) == true)
        {
            sb.AppendLine("    DETERMINISTIC");
        }

        if (procedure.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess) is { } dataAccess)
        {
            sb.Append("    ").AppendLine(dataAccess);
        }

        if (procedure.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker) == true)
        {
            sb.AppendLine("    SQL SECURITY INVOKER");
        }

        // The body is emitted verbatim and the statement is not terminated with a semicolon:
        // a BEGIN ... END body contains its own, and each delta is sent to the server as a
        // single command, so no DELIMITER handling is needed.
        sb.AppendLine(body);

        return sb.ToString();
    }

    private static string DropFunctionStatement(Element function)
    {
        if (function.Name is not string name)
        {
            throw new ArgumentException("Cannot drop a function without a name");
        }

        return $"DROP FUNCTION IF EXISTS {SqlName.Parse(name).Sql};{Environment.NewLine}";
    }

    private static string GenerateCreateFunctionScript(Element function)
    {
        if (function.Name is not string name)
        {
            throw new ArgumentException("Cannot create a function without a name");
        }

        var arguments = function.GetRequiredProperty<string>(MariaDbPropertyNames.Arguments);
        var returnType = function.GetRequiredProperty<string>(MariaDbPropertyNames.ReturnType);
        var body = function.GetRequiredProperty<string>(MariaDbPropertyNames.Body);

        var sb = new StringBuilder();

        sb.Append("CREATE FUNCTION ").Append(SqlName.Parse(name).Sql)
            .Append('(').Append(FunctionArguments(arguments)).Append(')')
            .Append(" RETURNS ").AppendLine(returnType);

        // Only non-default characteristics are stored on the element, so each is written only
        // when present — matching what the engines report for an unadorned function.
        if (function.GetProperty<bool?>(MariaDbPropertyNames.IsDeterministic) == true)
        {
            sb.AppendLine("    DETERMINISTIC");
        }

        if (function.GetProperty<string>(MariaDbPropertyNames.SqlDataAccess) is { } dataAccess)
        {
            sb.Append("    ").AppendLine(dataAccess);
        }

        if (function.GetProperty<bool?>(MariaDbPropertyNames.IsSecurityInvoker) == true)
        {
            sb.AppendLine("    SQL SECURITY INVOKER");
        }

        // The body — a RETURN ... or a BEGIN ... END — is emitted verbatim, and the statement
        // is not terminated with a semicolon: a BEGIN ... END body contains its own, and each
        // delta is sent as a single command, so no DELIMITER handling is needed.
        sb.AppendLine(body);

        return sb.ToString();
    }

    // A function's parameters are stored with an "IN " mode prefix, matching what both engines
    // report from information_schema (so a parsed model hash-matches an extracted one). But
    // CREATE FUNCTION syntax forbids a mode keyword on a parameter — a function parameter is
    // always IN — so the prefix is stripped when scripting the DDL.
    private static string FunctionArguments(string storedArguments)
    {
        if (storedArguments.Length == 0)
        {
            return storedArguments;
        }

        return string.Join(", ", storedArguments
            .Split(", ")
            .Select(a => a.StartsWith("IN ", StringComparison.Ordinal) ? a[3..] : a));
    }

    // ---- REBUILD ----

    protected override string ForeignKeyDropVerb => "DROP FOREIGN KEY";

    protected override string QuoteForeignKeyDefiningTable(string referencedName) =>
        SqlName.Parse(referencedName).Sql;

    protected override string QuoteConstraintName(string constraintName) =>
        SqlName.Parse(constraintName).QuotedUnqualified;

    // A rename-aside name for a rebuilt object, guaranteed to stay within MariaDB's 64-character
    // identifier limit. See <see cref="ScriptGeneratorBase.ComputeRebuildAsideName"/> for the
    // truncate-and-hash logic. Static so the unit tests can call it without a generator instance.
    public static string RebuildAsideName(string baseName) =>
        ComputeRebuildAsideName(baseName, static s => s.Length, 64);

    // Rebuilds a table that can't be altered in place: rename the existing table aside,
    // create the desired table, copy the shared columns, then drop the renamed original.
    // MariaDB DDL is not transactional (each statement auto-commits), so unlike the Postgres
    // provider this is not wrapped in BEGIN/COMMIT; a failure mid-rebuild leaves the renamed
    // original in place under its aside name for manual recovery.
    protected override string GenerateRebuildScript(RebuildTableDelta rebuildDelta)
    {
        if (rebuildDelta.SourceElement.Name is not string tableName)
        {
            throw new ArgumentException("Tables must have names");
        }

        var sourceName = SqlName.Parse(tableName);
        var quotedTableName = sourceName.Sql;

        var oldName = sourceName.Sibling(RebuildAsideName(sourceName.UnqualifiedName));
        var quotedOldName = oldName.Sql;

        var targetColumns = RelationshipHelpers.GetOrderedColumns(rebuildDelta.TargetElement)
            .ToDictionary(c => c.Name, c => c.Column);
        var carriedColumns = RelationshipHelpers.GetOrderedColumns(rebuildDelta.SourceElement)
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

    // ---- Foreign keys ----

    // CONSTRAINT `name` FOREIGN KEY (`a`, `b`) REFERENCES `table` (`x`, `y`)
    //   [ON DELETE <action>] [ON UPDATE <action>]
    protected override string GetForeignKeyClause(Element foreignKey)
    {
        if (foreignKey.Name is not string fkName)
        {
            throw new ArgumentException("Foreign keys must have names");
        }

        var columns = RelationshipHelpers.GetReferenceColumnNames(foreignKey, MariaDbRelationshipNames.ForeignKeyColumns);

        if (columns.Count == 0)
        {
            throw new InvalidOperationException($"Foreign key {fkName} has no referencing columns");
        }

        var foreignTableRef = foreignKey.GetRelationship(MariaDbRelationshipNames.ForeignTable)
            ?.Entries.OfType<Reference>().SingleOrDefault()
            ?? throw new InvalidOperationException($"Foreign key {fkName} has no referenced table");

        var foreignColumns = RelationshipHelpers.GetReferenceColumnNames(foreignKey, MariaDbRelationshipNames.ForeignColumns);

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

    // The quoted table name a constraint belongs to, from its DefiningTable reference.
    private static string ConstraintTableName(Element constraint)
    {
        var reference = constraint.GetRelationship(MariaDbRelationshipNames.DefiningTable)
            ?.Entries.OfType<Reference>().SingleOrDefault()
            ?? throw new InvalidOperationException(
                $"Constraint {constraint.Name} has no defining-table reference");

        return SqlName.Parse(reference.Name).Sql;
    }

    // The ADD-able clause for a table constraint, or null when the element is not one. Lets
    // the create and recreate paths dispatch on constraint-ness once rather than repeating the
    // same type test. A unique constraint is absent deliberately: this provider models one as a
    // unique SqlIndex, never as a SqlUniqueConstraint.
    private string? ConstraintClause(Element element) => element.Type switch
    {
        MariaDbElementTypes.SqlPrimaryKeyConstraint => GetPrimaryKeyClause(element),
        MariaDbElementTypes.SqlForeignKeyConstraint => GetForeignKeyClause(element),
        MariaDbElementTypes.SqlCheckConstraint => GetCheckConstraintClause(element),
        _ => null,
    };

    // PRIMARY KEY (`col`, ...), for a primary key written into a CREATE TABLE body or added to
    // an existing table by ALTER TABLE (issue #157).
    //
    // No CONSTRAINT name is emitted: both engines name the primary key `PRIMARY` regardless of
    // what the source calls it, so writing a name would be silently discarded — and would then
    // read back differently from what was declared.
    private static string GetPrimaryKeyClause(Element primaryKey)
    {
        // From the column specifications, so a declared prefix length survives (issue #161).
        var keys = RelationshipHelpers.GetColumnSpecifications(primaryKey)
            .Select(spec => RenderIndexKey(spec, "PRIMARY"))
            .ToList();

        if (keys.Count == 0)
        {
            throw new InvalidOperationException($"Primary key '{primaryKey.Name}' has no columns");
        }

        return $"PRIMARY KEY ({string.Join(", ", keys)})";
    }

    // The statement that removes a constraint from its table. Each kind has its own spelling on
    // these engines, unlike PostgreSQL's uniform DROP CONSTRAINT (issue #157):
    //
    //  * A primary key is dropped by keyword, with no name — the engine calls every one
    //    `PRIMARY`, so there is nothing else to name it by.
    //  * A foreign key needs DROP FOREIGN KEY. Plain DROP CONSTRAINT does exist on current
    //    MariaDB and MySQL, but DROP FOREIGN KEY is the spelling both have always accepted.
    //  * A CHECK is dropped with DROP CONSTRAINT, which is what MariaDB requires for one.
    //
    // Returns null for anything that is not a constraint.
    private static string? DropConstraintStatement(Element constraint)
    {
        if (constraint.Type == MariaDbElementTypes.SqlPrimaryKeyConstraint)
        {
            return $"ALTER TABLE {ConstraintTableName(constraint)} DROP PRIMARY KEY;"
                + Environment.NewLine;
        }

        if (constraint.Name is not string name)
        {
            return null;
        }

        var quotedName = SqlName.Parse(name).QuotedUnqualified;

        return constraint.Type switch
        {
            MariaDbElementTypes.SqlForeignKeyConstraint =>
                $"ALTER TABLE {ConstraintTableName(constraint)} DROP FOREIGN KEY {quotedName};"
                + Environment.NewLine,

            MariaDbElementTypes.SqlCheckConstraint =>
                $"ALTER TABLE {ConstraintTableName(constraint)} DROP CONSTRAINT {quotedName};"
                + Environment.NewLine,

            _ => null,
        };
    }

    // CONSTRAINT `name` CHECK (predicate), for a CHECK constraint written into a
    // CREATE TABLE or added by ALTER TABLE (issue #120).
    private static string GetCheckConstraintClause(Element checkConstraint)
    {
        if (checkConstraint.Name is not string checkName)
        {
            throw new ArgumentException("Check constraints must have names");
        }

        var expression = checkConstraint.GetProperty<string>(MariaDbPropertyNames.CheckExpression);

        if (string.IsNullOrWhiteSpace(expression))
        {
            throw new InvalidOperationException(
                $"Check constraint '{checkName}' has no expression");
        }

        return $"CONSTRAINT {SqlName.Parse(checkName).QuotedUnqualified} CHECK ({expression})";
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
        var collectionValues = typeElement.GetProperty<string?>(MariaDbPropertyNames.CollectionValues);

        var typeName = typeReference.Name.ToLowerInvariant();

        var rendered = typeName switch
        {
            "varchar" or "char" or "binary" or "varbinary" when maxLength != null
                => $"{typeName}({maxLength})",
            "decimal" or "numeric" when precision != null => $"{typeName}({precision}, {scale ?? 0})",
            "enum" or "set" when collectionValues != null => $"{typeName}{collectionValues}",
            // A fractional-seconds precision, e.g. datetime(3) (issue #144). Omitted when
            // absent, which is how a plain `datetime` is modeled.
            _ when precision != null && MariaDbTypeCategories.IsTemporalPrecisionType(typeName)
                => $"{typeName}({precision})",
            _ => typeName,
        };

        if (isUnsigned)
        {
            rendered += " unsigned";
        }

        return rendered;
    }
}
