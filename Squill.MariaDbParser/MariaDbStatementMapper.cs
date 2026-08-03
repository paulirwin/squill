using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Squill.MariaDbParser.Syntax;

namespace Squill.MariaDbParser;

/// <summary>
/// Maps ANTLR parse-tree contexts for the statements Squill models (CREATE TABLE, CREATE
/// INDEX) into the focused <see cref="Syntax"/> tree. Unrecognized statements map to null.
/// </summary>
internal static class MariaDbStatementMapper
{
    public static Statement? Map(MariaDBParser.DdlStatementContext ddl)
    {
        if (ddl.createTable() is { } createTable)
        {
            return MapCreateTable(createTable);
        }

        if (ddl.createIndex() is { } createIndex)
        {
            return MapCreateIndex(createIndex);
        }

        if (ddl.createProcedure() is { } createProcedure)
        {
            return MapCreateProcedure(createProcedure);
        }

        if (ddl.createView() is { } createView)
        {
            return MapCreateView(createView);
        }

        if (ddl.createFunction() is { } createFunction)
        {
            return MapCreateFunction(createFunction);
        }

        if (ddl.createTrigger() is { } createTrigger)
        {
            return MapCreateTrigger(createTrigger);
        }

        if (ddl.createEvent() is { } createEvent)
        {
            return MapCreateEvent(createEvent);
        }

        var description = DescribeDdl(ddl);

        // An authored ALTER/DROP/TRUNCATE is imperative: it has no meaning in a declarative
        // project, so it is rejected with its own SQ0006 error rather than the SQ1002 warning
        // the rest of this branch produces. A warning was the wrong signal twice over — it
        // blamed a gap in Squill for a mistake in the source, and it let the build succeed
        // while the statement was silently discarded (issue #125).
        if (IsImperativeDdl(description))
        {
            // TRUNCATE's second word is the table name, not a keyword ("TRUNCATE TABLE" only
            // when TABLE is written), so it is named by its verb alone — matching Postgres, so
            // the same mistake reads the same on both engines.
            var name = description.StartsWith("TRUNCATE", StringComparison.Ordinal)
                ? "TRUNCATE"
                : description;

            return At(new ImperativeStatement(name, ImperativeKind.SchemaChange), ddl);
        }

        // Any other unrecognized DDL is not modeled. It becomes a marker statement rather than
        // being dropped, so the model builder can warn that it will not reach the DACPAC
        // instead of the construct silently vanishing (issue #61).
        return At(new UnmodeledStatement(description), ddl);
    }

    // Statements that write data. A CTE takes the kind of the statement it feeds rather than
    // of the leading WITH, so these are matched anywhere in the statement: a
    // `WITH x AS (…) INSERT …` writes data and must get the seed-data remedy, while a
    // `WITH x AS (…) SELECT …` is only a query.
    private static readonly HashSet<string> DataChangeKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "REPLACE", "LOAD",
    ];

    /// <summary>
    /// Maps a DML statement (INSERT/UPDATE/DELETE/SELECT/…) to the marker the builder rejects.
    /// DML never reached this mapper before — <c>EnumerateStatements</c> only yielded DDL — so
    /// a stray INSERT in a source file vanished with no diagnostic at all (issue #125).
    /// </summary>
    public static Statement Map(MariaDBParser.DmlStatementContext dml)
    {
        var keywords = Keywords(dml).ToList();

        // Just the verb: "INSERT INTO" adds nothing over "INSERT", and what follows is the
        // table name rather than a keyword.
        var name = keywords.Count > 0 ? keywords[0] : "This statement";

        // A query is rejected like everything else — it declares nothing, so it has no business
        // in a schema file — but nothing is written, so it does not get the "move this into a
        // post-deploy script" remedy, which would be advising the author to keep a statement
        // that does nothing either way.
        var kind = keywords.Any(DataChangeKeywords.Contains)
            ? ImperativeKind.DataChange
            : ImperativeKind.Query;

        return At(new ImperativeStatement(name, kind), dml);
    }

    // Statements that change schema rather than declare it, matched on the leading keyword.
    private static bool IsImperativeDdl(string description)
    {
        var first = description.Split(' ')[0];

        return first is "ALTER" or "DROP" or "TRUNCATE" or "RENAME";
    }

    // The word-shaped tokens of a statement, upper-cased, in source order.
    private static IEnumerable<string> Keywords(MariaDBParser.DmlStatementContext dml)
        => Trees.Descendants(dml)
            .OfType<ITerminalNode>()
            .Select(i => i.Symbol.Text)
            .Where(i => !string.IsNullOrWhiteSpace(i) && i.All(char.IsLetter))
            .Select(i => i.ToUpperInvariant());

    // A short, human-readable name for an unmodeled DDL statement: its first two tokens
    // (CREATE VIEW, ALTER TABLE, DROP INDEX, …), so the warning names what was written.
    private static string DescribeDdl(MariaDBParser.DdlStatementContext ddl)
    {
        var keywords = Trees.Descendants(ddl)
            .OfType<ITerminalNode>()
            .Select(i => i.Symbol.Text)
            .Where(i => !string.IsNullOrWhiteSpace(i))
            .Take(2)
            .ToList();

        return keywords.Count > 0
            ? string.Join(' ', keywords).ToUpperInvariant()
            : "statement";
    }

    // ---- CREATE TABLE ----

    private static Statement? MapCreateTable(MariaDBParser.CreateTableContext createTable)
    {
        // Only the column-list form (CREATE TABLE t (...)) is modeled; CREATE TABLE ... AS
        // SELECT and CREATE TABLE ... LIKE describe no standalone column shape here. They
        // become markers so the builder can warn rather than dropping them silently.
        if (createTable is not MariaDBParser.ColumnCreateTableContext columnCreate)
        {
            return At(new UnmodeledStatement("CREATE TABLE (copy form)"), createTable);
        }

        var name = MapQualifiedName(columnCreate.tableName().fullId());
        var statement = At(new CreateTableStatement(name), columnCreate);

        foreach (var definition in columnCreate.createDefinitions().createDefinition())
        {
            switch (definition)
            {
                case MariaDBParser.ColumnDeclarationContext column:
                    statement.Elements.Add(MapColumn(column));
                    break;

                case MariaDBParser.ConstraintDeclarationContext constraint:
                    statement.Elements.Add(MapTableConstraint(constraint.tableConstraint()));
                    break;

                case MariaDBParser.IndexDeclarationContext index:
                    statement.Elements.Add(MapInlineIndex(index.indexColumnDefinition()));
                    break;

                // periodDeclaration and anything else is ignored.
            }
        }

        return statement;
    }

    private static ColumnDefinition MapColumn(MariaDBParser.ColumnDeclarationContext column)
    {
        var name = new Identifier(UidText(column.uid()));
        var definition = column.columnDefinition();
        var dataType = MapDataType(definition.dataType());

        var columnDefinition = new ColumnDefinition(name, dataType);

        foreach (var constraint in definition.columnConstraint())
        {
            columnDefinition.Constraints.Add(MapColumnConstraint(constraint));
        }

        return columnDefinition;
    }

    private static ColumnConstraint MapColumnConstraint(MariaDBParser.ColumnConstraintContext constraint)
    {
        switch (constraint)
        {
            case MariaDBParser.NullColumnConstraintContext nullConstraint:
                // nullNotnull is `NOT? NULL`; presence of NOT means NOT NULL.
                var notNull = nullConstraint.nullNotnull().NOT() != null;
                return new NullableColumnConstraint(!notNull);

            case MariaDBParser.PrimaryKeyColumnConstraintContext:
                return new PrimaryKeyColumnConstraint();

            case MariaDBParser.UniqueKeyColumnConstraintContext:
                return new UniqueKeyColumnConstraint();

            case MariaDBParser.AutoIncrementColumnConstraintContext autoInc
                when autoInc.AUTO_INCREMENT() != null:
                return new AutoIncrementColumnConstraint();

            case MariaDBParser.DefaultColumnConstraintContext defaultConstraint:
                return MapDefault(defaultConstraint.defaultValue());

            case MariaDBParser.ReferenceColumnConstraintContext reference:
                return At(MapInlineForeignKey(reference.referenceDefinition()), reference);

            case MariaDBParser.CheckColumnConstraintContext check:
            {
                // An inline CHECK; a `CONSTRAINT <uid> CHECK (...)` wrapper names it. The
                // predicate is kept as source text so it can be scripted back out (#120).
                var checkConstraint = At(
                    new CheckColumnConstraint(SourceText(check.expression())), check);

                return check.name is { } name
                    ? At(new NamedColumnConstraint(UidText(name), checkConstraint), check)
                    : checkConstraint;
            }

            case MariaDBParser.GeneratedColumnConstraintContext generated
                when generated.expression() is { } generationExpression:
            {
                // `AS (expr) [VIRTUAL|STORED|PERSISTENT]` — a generated column (issue #120).
                // MariaDB defaults to VIRTUAL when no storage kind is written; PERSISTENT is
                // its older synonym for STORED. The `AS ROW START|END` form of this rule is a
                // system-versioning period column, not a generated one, and has no
                // expression — the `when` guard above routes it to the ignored path.
                var isStored = generated.STORED() != null || generated.PERSISTENT() != null;

                return At(new GeneratedColumnConstraint(
                    SourceText(generationExpression), isStored), generated);
            }

            // COMMENT, COLLATE, VISIBLE, … are recognized but not modeled. (ON UPDATE
            // CURRENT_TIMESTAMP is not among them: the grammar makes it part of the DEFAULT
            // clause, handled by MapDefault above.)
            default:
                return new IgnoredColumnConstraint();
        }
    }

    /// <summary>
    /// Maps a <c>DEFAULT</c> clause. The grammar's <c>defaultValue</c> production covers the
    /// value and an optional trailing <c>ON UPDATE CURRENT_TIMESTAMP</c> in one rule
    /// (<c>currentTimestamp (ON UPDATE currentTimestamp)?</c>), so taking the whole rule's text
    /// would run the two together into <c>CURRENT_TIMESTAMPONUPDATECURRENT_TIMESTAMP</c> — a
    /// token no canonicalizer could recognize. Read the parts separately instead.
    /// </summary>
    private static DefaultColumnConstraint MapDefault(MariaDBParser.DefaultValueContext defaultValue)
    {
        var timestamps = defaultValue.currentTimestamp();

        // The `currentTimestamp (ON UPDATE currentTimestamp)?` alternative: the first is the
        // default, a second (present only with ON UPDATE) is the auto-refresh clause. Both are
        // carried verbatim — the rule admits a precision (CURRENT_TIMESTAMP(3)) and several
        // function spellings, and it is the provider's job to decide which of those it can
        // model, not the parser's to discard them.
        if (timestamps.Length > 0)
        {
            return new DefaultColumnConstraint(
                timestamps[0].GetText(),
                onUpdateToken: timestamps.Length > 1 ? timestamps[1].GetText() : null);
        }

        return new DefaultColumnConstraint(defaultValue.GetText());
    }

    private static ForeignKeyColumnConstraint MapInlineForeignKey(
        MariaDBParser.ReferenceDefinitionContext reference)
    {
        var referencedTable = MapQualifiedName(reference.tableName().fullId());

        Identifier? referencedColumn = null;
        if (reference.indexColumnNames() is { } columnNames)
        {
            var columns = ForeignKeyColumns(columnNames);
            referencedColumn = columns.Count > 0 ? columns[0] : null;
        }

        var (onDelete, onUpdate) = MapReferenceActions(reference.referenceAction());

        return new ForeignKeyColumnConstraint(referencedTable, referencedColumn, onDelete, onUpdate);
    }

    // ---- Table constraints ----

    private static ITableElement MapTableConstraint(MariaDBParser.TableConstraintContext constraint)
    {
        switch (constraint)
        {
            case MariaDBParser.PrimaryKeyTableConstraintContext pk:
            {
                var name = ConstraintName(pk.CONSTRAINT() != null, pk.uid());
                var columns = MapIndexColumnNames(pk.indexColumnNames());
                return Wrap(name, At(new PrimaryKeyTableConstraint(columns), pk));
            }

            case MariaDBParser.UniqueKeyTableConstraintContext unique:
            {
                var name = ConstraintName(unique.CONSTRAINT() != null, unique.uid());
                var columns = MapIndexColumnNames(unique.indexColumnNames());
                // The index name (if any) is the uid that is NOT the constraint name.
                var indexName = IndexNameFromUids(unique.CONSTRAINT() != null, unique.uid());
                return Wrap(name, At(new UniqueKeyTableConstraint(indexName, columns), unique));
            }

            case MariaDBParser.ForeignKeyTableConstraintContext fk:
            {
                var name = ConstraintName(fk.CONSTRAINT() != null, fk.uid());
                var columns = ForeignKeyColumns(fk.indexColumnNames());
                var reference = fk.referenceDefinition();
                var referencedTable = MapQualifiedName(reference.tableName().fullId());
                var referencedColumns = reference.indexColumnNames() is { } refCols
                    ? ForeignKeyColumns(refCols)
                    : new List<Identifier>();
                var (onDelete, onUpdate) = MapReferenceActions(reference.referenceAction());

                return Wrap(name, At(new ForeignKeyTableConstraint(
                    columns, referencedTable, referencedColumns, onDelete, onUpdate), fk));
            }

            case MariaDBParser.CheckTableConstraintContext check:
            {
                // The check rule labels its single optional uid as `name`, so unlike the
                // PK/UNIQUE rules there is no trailing index name to disambiguate.
                var name = check.name is { } uid ? UidText(uid) : null;

                return Wrap(name, At(
                    new CheckTableConstraint(SourceText(check.expression())), check));
            }

            // FULLTEXT, SPATIAL and anything else is recognized but not modeled.
            default:
                return new IgnoredTableConstraint();
        }
    }

    private static ITableElement Wrap(string? name, TableConstraint constraint)
        => name is null
            ? constraint
            : new NamedTableConstraint(name, constraint) { Line = constraint.Line, Column = constraint.Column };

    // Stamps a node with the 1-based line/column where its source context starts, so later
    // phases (model building, reference validation) can report diagnostics that point back
    // into the source file (issue #53).
    private static T At<T>(T node, ParserRuleContext context) where T : SyntaxNode
    {
        node.Line = context.Start.Line;
        node.Column = context.Start.Column + 1;
        return node;
    }

    // The explicit CONSTRAINT name, if the constraint was written `CONSTRAINT <uid> ...`.
    // In the grammar the first uid after CONSTRAINT is the constraint name.
    private static string? ConstraintName(bool hasConstraintKeyword, MariaDBParser.UidContext[] uids)
        => hasConstraintKeyword && uids.Length > 0 ? UidText(uids[0]) : null;

    // The index name for a UNIQUE KEY: the trailing uid that names the index (distinct from
    // a leading CONSTRAINT name), or null when none is written.
    private static string? IndexNameFromUids(bool hasConstraintKeyword, MariaDBParser.UidContext[] uids)
    {
        var startIndex = hasConstraintKeyword ? 1 : 0;
        return uids.Length > startIndex ? UidText(uids[startIndex]) : null;
    }

    private static ITableElement MapInlineIndex(MariaDBParser.IndexColumnDefinitionContext index)
    {
        if (index is MariaDBParser.SimpleIndexDeclarationContext simple)
        {
            var indexName = simple.uid() is { } uid ? UidText(uid) : null;
            // As in CREATE INDEX, USING may be written either before the column list (bound
            // to indexType()) or after it, where the grammar folds it into indexOption*.
            var method = MapIndexType(simple.indexType())
                ?? simple.indexOption()
                    .Select(option => MapIndexType(option.indexType()))
                    .FirstOrDefault(m => m is not null);
            var columns = MapIndexColumnNames(simple.indexColumnNames());
            return new IndexTableConstraint(indexName, method, columns);
        }

        // A FULLTEXT / SPATIAL index (issue #146). The kind is carried as its own token rather
        // than folded into the index method: both engines reject `USING FULLTEXT` outright, so
        // the two cannot share a slot.
        if (index is MariaDBParser.SpecialIndexDeclarationContext special)
        {
            var kind = special.FULLTEXT() != null ? "FULLTEXT" : "SPATIAL";
            var indexName = special.uid() is { } specialUid ? UidText(specialUid) : null;
            var columns = MapIndexColumnNames(special.indexColumnNames());

            // No index method: these kinds take no USING clause.
            return new IndexTableConstraint(indexName, indexMethod: null, columns, kind);
        }

        return new IgnoredTableConstraint();
    }

    // ---- CREATE INDEX ----

    private static Statement MapCreateIndex(MariaDBParser.CreateIndexContext createIndex)
    {
        var name = UidText(createIndex.uid());
        var onTable = MapQualifiedName(createIndex.tableName().fullId());

        // MariaDB accepts USING either before the ON clause (`CREATE INDEX i USING BTREE ON
        // t (a)`) or after the column list, where the grammar folds it into indexOption*
        // (`CREATE INDEX i ON t (a) USING BTREE`). Only the first is bound to indexType(),
        // so fall back to the trailing option; without it the method is silently dropped.
        var indexMethod = MapIndexType(createIndex.indexType())
            ?? createIndex.indexOption()
                .Select(option => MapIndexType(option.indexType()))
                .FirstOrDefault(method => method is not null);

        // FULLTEXT / SPATIAL are alternatives of the same `indexCategory` slot as UNIQUE, so at
        // most one of the three is ever written (issue #146).
        var indexKind = createIndex.FULLTEXT() != null ? "FULLTEXT"
            : createIndex.SPATIAL() != null ? "SPATIAL"
            : null;

        var statement = At(new CreateIndexStatement(name, onTable)
        {
            Unique = createIndex.UNIQUE() != null,
            IndexMethod = indexMethod,
            IndexKind = indexKind,
        }, createIndex);

        foreach (var column in MapIndexColumnNames(createIndex.indexColumnNames()))
        {
            statement.Columns.Add(column);
        }

        return statement;
    }

    // ---- CREATE VIEW ----

    // createView
    //   : CREATE orReplace? (ALGORITHM '=' algType)? ownerStatement? (SQL SECURITY secContext)?
    //     VIEW fullId ('(' uidList ')')? AS
    //     ( '(' withClause? selectStatement ')' | withClause? selectStatement (WITH ... CHECK OPTION)? )
    //
    // Only the facets that make up a view's modeled identity are pulled out — its name, its
    // column list, and the tables it selects from — plus the query text, carried verbatim
    // for scripting. See CreateViewStatement for why the body cannot participate in the model.
    private static Statement MapCreateView(MariaDBParser.CreateViewContext createView)
    {
        var statement = At(
            new CreateViewStatement(
                MapQualifiedName(createView.fullId()),
                createView.orReplace() is not null),
            createView);

        if (createView.uidList() is { } columnList)
        {
            foreach (var uid in columnList.uid())
            {
                statement.ColumnNames.Add(new Identifier(UidText(uid)));
            }
        }

        var select = createView.selectStatement();

        statement.Body = SourceText(select);

        var query = FirstQuerySpecification(select);

        if (query is null)
        {
            throw new NotSupportedException(
                "A view over this form of query is not yet supported; "
                + "only a SELECT with an explicit select list is modeled");
        }

        foreach (var column in MapSelectElements(query))
        {
            statement.SelectColumns.Add(column);
        }

        foreach (var table in MapSourceTables(query))
        {
            statement.SourceTables.Add(table);
        }

        return statement;
    }

    private static IEnumerable<ViewSelectColumn> MapSelectElements(ParserRuleContext query)
    {
        var selectElements = query.GetRuleContext<MariaDBParser.SelectElementsContext>(0);

        if (selectElements is null)
        {
            yield break;
        }

        // `SELECT *` is a bare star token on selectElements, not a selectElement.
        if (selectElements.STAR() is not null)
        {
            yield return ViewSelectColumn.Wildcard();
        }

        foreach (var element in selectElements.selectElement())
        {
            yield return MapSelectElement(element);
        }
    }

    private static ViewSelectColumn MapSelectElement(MariaDBParser.SelectElementContext element)
    {
        switch (element)
        {
            // `t.*`
            case MariaDBParser.SelectStarElementContext star:
                return ViewSelectColumn.Wildcard(MapQualifiedName(star.fullId()).Name);

            case MariaDBParser.SelectColumnElementContext column:
                // An explicit alias always wins over the column's own name.
                if (column.uid() is { } columnAlias)
                {
                    return ViewSelectColumn.Aliased(UidText(columnAlias));
                }

                return MapFullColumnName(column.fullColumnName());

            case MariaDBParser.SelectFunctionElementContext function:
                return function.uid() is { } functionAlias
                    ? ViewSelectColumn.Aliased(UidText(functionAlias))
                    : ViewSelectColumn.Unnamed();

            case MariaDBParser.SelectExpressionElementContext expression:
                return expression.uid() is { } expressionAlias
                    ? ViewSelectColumn.Aliased(UidText(expressionAlias))
                    : ViewSelectColumn.Unnamed();

            default:
                return ViewSelectColumn.Unnamed();
        }
    }

    // fullColumnName : uid (dottedId dottedId?)? — the last segment is the column, anything
    // before it qualifies it (table, or database and table).
    private static ViewSelectColumn MapFullColumnName(MariaDBParser.FullColumnNameContext fullColumnName)
    {
        var dottedIds = fullColumnName.dottedId();

        if (fullColumnName.uid() is not { } uid)
        {
            return ViewSelectColumn.Unnamed();
        }

        var first = UidText(uid);

        if (dottedIds.Length == 0)
        {
            return ViewSelectColumn.Named(first);
        }

        // `table.column` — the qualifier is the leading uid.
        // `db.table.column` — the qualifier is the table, the middle segment.
        var segments = dottedIds.Select(DottedIdText).ToList();

        return ViewSelectColumn.Named(
            segments[^1],
            segments.Count == 1 ? first : segments[^2]);
    }

    private static IEnumerable<QualifiedName> MapSourceTables(ParserRuleContext query)
    {
        var fromClause = query.GetRuleContext<MariaDBParser.FromClauseContext>(0);

        if (fromClause?.tableSources() is not { } tableSources)
        {
            yield break;
        }

        foreach (var tableSource in tableSources.tableSource())
        {
            // Only a plain table reference names a table Squill can look up; a subquery or
            // a nested source does not.
            foreach (var atom in Descendants<MariaDBParser.AtomTableItemContext>(tableSource))
            {
                yield return MapQualifiedName(atom.tableName().fullId());
            }
        }
    }

    // The first querySpecification (or querySpecificationNointo) in a possibly parenthesized
    // or UNION-ed query. A set operation takes its column names from the first branch, which
    // is how both engines name them.
    private static ParserRuleContext? FirstQuerySpecification(IParseTree node)
    {
        switch (node)
        {
            case MariaDBParser.QuerySpecificationContext query:
                return query;

            case MariaDBParser.QuerySpecificationNointoContext queryNointo:
                return queryNointo;
        }

        for (var i = 0; i < node.ChildCount; i++)
        {
            if (FirstQuerySpecification(node.GetChild(i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static IEnumerable<T> Descendants<T>(IParseTree node) where T : ParserRuleContext
    {
        if (node is T match)
        {
            yield return match;
        }

        for (var i = 0; i < node.ChildCount; i++)
        {
            foreach (var found in Descendants<T>(node.GetChild(i)))
            {
                yield return found;
            }
        }
    }

    // A dottedId is written ".name" (or ".`name`"); strip the leading dot before unquoting.
    private static string DottedIdText(MariaDBParser.DottedIdContext dottedId)
        => TrimIdentifier(dottedId.GetText().TrimStart('.'));

    // ---- Shared helpers ----

    private static (ReferentialAction? OnDelete, ReferentialAction? OnUpdate) MapReferenceActions(
        MariaDBParser.ReferenceActionContext? action)
    {
        if (action is null)
        {
            return (null, null);
        }

        // referenceAction is `ON DELETE ctl (ON UPDATE ctl)?` or `ON UPDATE ctl (ON DELETE
        // ctl)?`; the grammar assigns onDelete / onUpdate labels, but they are exposed as
        // ReferenceControlTypeContext[] with ON/DELETE/UPDATE tokens. Pair each control type
        // with whether a DELETE or UPDATE token precedes it.
        ReferentialAction? onDelete = null;
        ReferentialAction? onUpdate = null;

        var controls = action.referenceControlType();
        var onTokens = action.ON();
        var deleteToken = action.DELETE();

        // Determine ordering: if DELETE appears (it always does in arm 1) map the first
        // control to it; otherwise UPDATE comes first. We walk children in order to pair
        // each ON <kind> with its control type robustly.
        var kinds = new List<string>();
        for (var i = 0; i < action.ChildCount; i++)
        {
            var text = action.GetChild(i).GetText();
            if (string.Equals(text, "DELETE", StringComparison.OrdinalIgnoreCase))
            {
                kinds.Add("DELETE");
            }
            else if (string.Equals(text, "UPDATE", StringComparison.OrdinalIgnoreCase))
            {
                kinds.Add("UPDATE");
            }
        }

        for (var i = 0; i < controls.Length && i < kinds.Count; i++)
        {
            var mapped = MapReferenceControl(controls[i]);
            if (kinds[i] == "DELETE")
            {
                onDelete = mapped;
            }
            else
            {
                onUpdate = mapped;
            }
        }

        return (onDelete, onUpdate);
    }

    private static ReferentialAction MapReferenceControl(MariaDBParser.ReferenceControlTypeContext control)
    {
        if (control.CASCADE() != null)
        {
            return ReferentialAction.Cascade;
        }

        if (control.SET() != null && control.NULL_LITERAL() != null)
        {
            return ReferentialAction.SetNull;
        }

        if (control.NO() != null && control.ACTION() != null)
        {
            // MariaDB treats NO ACTION as RESTRICT.
            return ReferentialAction.Restrict;
        }

        // RESTRICT (or the default).
        return ReferentialAction.Restrict;
    }

    /// <summary>
    /// The column names of a FOREIGN KEY's key list. A foreign key references whole columns —
    /// neither engine accepts a prefix length or an expression in one — so the extra facets
    /// <see cref="MapIndexColumnNames"/> now reads (issue #161) do not apply here, and a key
    /// that is not a plain column is skipped rather than modeled as one.
    /// </summary>
    private static List<Identifier> ForeignKeyColumns(MariaDBParser.IndexColumnNamesContext columnNames)
        => MapIndexColumnNames(columnNames)
            .Select(c => c.Column)
            .OfType<Identifier>()
            .ToList();

    private static IReadOnlyList<IndexColumn> MapIndexColumnNames(MariaDBParser.IndexColumnNamesContext columnNames)
    {
        var columns = new List<IndexColumn>();

        foreach (var columnName in columnNames.indexColumnName())
        {
            bool? isAscending = columnName.ASC() != null ? true
                : columnName.DESC() != null ? false
                : null;

            // An expression key — the `(a + b)` in `CREATE INDEX ix ON t ((a + b))` (issue
            // #161). It names no column, so it is carried as text instead. Dropping it used to
            // deploy an index with fewer keys than declared, silently.
            if (columnName.uid() is not { } uid)
            {
                if (columnName.expression() is not { } expression)
                {
                    // A STRING_LITERAL key: neither a column nor an expression, and nothing in
                    // either engine produces one from a CREATE. Skipping it is what the mapper
                    // has always done.
                    continue;
                }

                columns.Add(new IndexColumn(
                    column: null, isAscending, keyExpression: SourceText(expression)));

                continue;
            }

            // The `(20)` in `Brand(20)`. The grammar allows a prefix only on a uid or string
            // key, never on an expression, so it is read only on this branch.
            var prefixLength = columnName.decimalLiteral() is { } prefix
                && int.TryParse(prefix.GetText(), out var length)
                    ? length
                    : (int?)null;

            columns.Add(new IndexColumn(new Identifier(UidText(uid)), isAscending, prefixLength));
        }

        return columns;
    }

    private static string? MapIndexType(MariaDBParser.IndexTypeContext? indexType)
    {
        if (indexType is null)
        {
            return null;
        }

        // indexType is `USING (BTREE | HASH | RTREE)`.
        if (indexType.BTREE() != null)
        {
            return "BTREE";
        }

        if (indexType.HASH() != null)
        {
            return "HASH";
        }

        if (indexType.RTREE() != null)
        {
            return "RTREE";
        }

        return null;
    }

    // ---- CREATE PROCEDURE ----

    private static Statement MapCreateProcedure(MariaDBParser.CreateProcedureContext createProcedure)
    {
        var statement = At(
            new CreateProcedureStatement(
                MapQualifiedName(createProcedure.fullId()),
                createProcedure.orReplace() is not null),
            createProcedure);

        foreach (var parameter in createProcedure.procedureParameter())
        {
            statement.Parameters.Add(At(
                new RoutineParameter(
                    new Identifier(UidText(parameter.uid())),
                    MapParameterMode(parameter.direction),
                    MapDataType(parameter.dataType())),
                parameter));
        }

        foreach (var option in createProcedure.routineOption())
        {
            ApplyRoutineOption(option,
                deterministic => statement.IsDeterministic = deterministic,
                dataAccess => statement.SqlDataAccess = dataAccess,
                securityInvoker => statement.IsSecurityInvoker = securityInvoker);
        }

        statement.Body = SourceText(createProcedure.routineBody());

        return statement;
    }

    private static Statement MapCreateFunction(MariaDBParser.CreateFunctionContext createFunction)
    {
        var statement = At(
            new CreateFunctionStatement(
                MapQualifiedName(createFunction.fullId()),
                MapDataType(createFunction.dataType()),
                createFunction.orReplace() is not null),
            createFunction);

        // A function parameter is always IN — the grammar's functionParameter rule carries no
        // direction — so each is mapped with the default mode.
        foreach (var parameter in createFunction.functionParameter())
        {
            statement.Parameters.Add(At(
                new RoutineParameter(
                    new Identifier(UidText(parameter.uid())),
                    ParameterMode.In,
                    MapDataType(parameter.dataType())),
                parameter));
        }

        foreach (var option in createFunction.routineOption())
        {
            ApplyRoutineOption(option,
                deterministic => statement.IsDeterministic = deterministic,
                dataAccess => statement.SqlDataAccess = dataAccess,
                securityInvoker => statement.IsSecurityInvoker = securityInvoker);
        }

        // A function body is either a routine body (BEGIN ... END) or a bare RETURN statement.
        // Both engines report ROUTINE_DEFINITION as whichever was written, verbatim.
        statement.Body = createFunction.routineBody() is { } body
            ? SourceText(body)
            : SourceText(createFunction.returnStatement());

        return statement;
    }

    // ---- CREATE TRIGGER ----

    private static Statement MapCreateTrigger(MariaDBParser.CreateTriggerContext createTrigger)
    {
        // The grammar labels the trigger's own name `thisTrigger`, its timing (BEFORE/AFTER)
        // `triggerTime`, and its event (INSERT/UPDATE/DELETE) `triggerEvent`. Both engines
        // report timing and event upper-cased in information_schema.TRIGGERS, so the tokens
        // are upper-cased here to match.
        var statement = At(
            new CreateTriggerStatement(
                MapQualifiedName(createTrigger.thisTrigger),
                createTrigger.triggerTime.Text.ToUpperInvariant(),
                createTrigger.triggerEvent.Text.ToUpperInvariant(),
                MapQualifiedName(createTrigger.tableName().fullId()),
                createTrigger.orReplace() is not null),
            createTrigger);

        // The body — a BEGIN ... END block or a single statement — is held verbatim, exactly
        // as ACTION_STATEMENT reports it, so a parsed model hash-matches an extracted one.
        statement.Body = SourceText(createTrigger.routineBody());

        return statement;
    }

    // ---- CREATE EVENT ----

    private static Statement MapCreateEvent(MariaDBParser.CreateEventContext createEvent)
    {
        var name = MapQualifiedName(createEvent.fullId());
        var schedule = createEvent.scheduleExpression();

        var statement = At(MapSchedule(schedule, name), createEvent);

        // ON COMPLETION NOT PRESERVE is the default on both engines, so only the PRESERVE
        // form sets the flag. The grammar hangs NOT off the createEvent rule itself.
        statement.PreserveOnCompletion =
            createEvent.PRESERVE() is not null && createEvent.NOT() is null;

        if (createEvent.enableType() is { } enableType)
        {
            statement.Status = MapEnableType(enableType);
        }

        if (createEvent.STRING_LITERAL() is { } comment)
        {
            statement.Comment = TrimStringLiteral(comment.GetText());
        }

        // The body is held verbatim, exactly as EVENT_DEFINITION reports it, so a parsed
        // model hash-matches an extracted one.
        statement.Body = SourceText(createEvent.routineBody());

        return statement;
    }

    // Builds the statement from its ON SCHEDULE clause, which decides whether the event is
    // ONE TIME or RECURRING — the two forms carry disjoint schedule facets, and the catalog
    // reports them in exactly those two shapes.
    //
    // Schedule values are recorded as written, including forms Squill cannot model (a
    // non-constant timestamp, a missing STARTS). Rejecting them is the model builder's job,
    // not the parser's: the builder catches per-statement failures and re-reports them with
    // the source file and position, so the user gets a diagnostic that points at the
    // offending statement instead of a bare exception.
    private static CreateEventStatement MapSchedule(
        MariaDBParser.ScheduleExpressionContext schedule,
        QualifiedName name)
    {
        switch (schedule)
        {
            case MariaDBParser.PreciseScheduleContext precise:
            {
                // `AT <value> (+ INTERVAL ...)*`. A trailing + INTERVAL is folded into the
                // recorded text so the builder can see — and reject — the whole expression.
                var executeAt = TimestampText(precise.timestampValue());

                foreach (var offset in precise.intervalExpr())
                {
                    executeAt += $" {SourceText(offset)}";
                }

                return new CreateEventStatement(name, "ONE TIME") { ExecuteAt = executeAt };
            }

            case MariaDBParser.IntervalScheduleContext interval:
            {
                // `EVERY <value> <unit> [STARTS ...] [ENDS ...]`.
                var statement = new CreateEventStatement(name, "RECURRING")
                {
                    IntervalValue = MapIntervalValue(interval),
                    // The catalog reports INTERVAL_FIELD upper-cased.
                    IntervalField = interval.intervalType().GetText().ToUpperInvariant(),
                };

                if (interval.startTimestamp is { } startTimestamp)
                {
                    statement.Starts = TimestampText(
                        startTimestamp, interval._startIntervals);
                }

                if (interval.endTimestamp is { } endTimestamp)
                {
                    statement.Ends = TimestampText(endTimestamp, interval._endIntervals);
                }

                return statement;
            }

            default:
                throw new NotSupportedException(
                    $"Event '{name.Name}' uses an unsupported ON SCHEDULE form");
        }
    }

    // The EVERY value. A plain count is written bare (EVERY 1 DAY); a compound interval is
    // written as a quoted, colon-separated literal (EVERY '2:3' DAY_HOUR) which the catalog
    // reports space-separated ('2 3'), so it is normalized here to the catalog's spelling.
    // A computed interval is recorded as written, for the builder to reject.
    private static string MapIntervalValue(MariaDBParser.IntervalScheduleContext interval)
    {
        if (interval.decimalLiteral() is { } decimalLiteral)
        {
            return decimalLiteral.GetText();
        }

        var text = interval.expression().GetText();

        return IsQuotedLiteral(text)
            ? TrimStringLiteral(text).Replace(':', ' ')
            : text;
    }

    // A schedule timestamp's text. A quoted literal is unquoted to the value the catalog
    // reports; anything else (CURRENT_TIMESTAMP, an expression) is kept verbatim so the
    // builder can reject it by name. Any + INTERVAL offsets are appended for the same reason.
    private static string TimestampText(
        MariaDBParser.TimestampValueContext timestamp,
        IList<MariaDBParser.IntervalExprContext>? offsets = null)
    {
        var text = timestamp.GetText();

        var result = timestamp.stringLiteral() is not null && IsQuotedLiteral(text)
            ? TrimStringLiteral(text)
            : text;

        if (offsets is not null)
        {
            foreach (var offset in offsets)
            {
                result += $" {SourceText(offset)}";
            }
        }

        return result;
    }

    private static string MapEnableType(MariaDBParser.EnableTypeContext enableType)
    {
        // The catalog spells these ENABLED / DISABLED / SLAVESIDE_DISABLED. MySQL reports
        // DISABLE ON SLAVE as REPLICA_SIDE_DISABLED instead; the extractor normalizes that
        // onto the MariaDB spelling recorded here, so one declaration matches both engines.
        if (enableType.ENABLE() is not null)
        {
            return "ENABLED";
        }

        return enableType.SLAVE() is not null ? "SLAVESIDE_DISABLED" : "DISABLED";
    }

    private static bool IsQuotedLiteral(string text)
        => text.Length >= 2
            && ((text[0] == '\'' && text[^1] == '\'') || (text[0] == '"' && text[^1] == '"'));

    // The text of a quoted string literal, with the quotes removed and doubled quotes
    // unescaped.
    private static string TrimStringLiteral(string text)
    {
        if (!IsQuotedLiteral(text))
        {
            return text;
        }

        var quote = text[0];
        return text[1..^1].Replace($"{quote}{quote}", $"{quote}");
    }

    private static ParameterMode MapParameterMode(IToken? direction)
        => direction?.Type switch
        {
            null => ParameterMode.In,
            MariaDBParser.OUT => ParameterMode.Out,
            MariaDBParser.INOUT => ParameterMode.InOut,
            _ => ParameterMode.In,
        };

    // Applies a routine characteristic clause via setters, so both CREATE PROCEDURE and
    // CREATE FUNCTION share the same option handling despite being distinct statement types.
    private static void ApplyRoutineOption(
        MariaDBParser.RoutineOptionContext option,
        Action<bool> setDeterministic,
        Action<string> setSqlDataAccess,
        Action<bool> setSecurityInvoker)
    {
        switch (option)
        {
            case MariaDBParser.RoutineBehaviorContext behavior:
                // `NOT DETERMINISTIC` is the default; only a bare DETERMINISTIC sets it.
                setDeterministic(behavior.NOT() is null);
                break;

            case MariaDBParser.RoutineDataContext data:
                setSqlDataAccess(SqlDataAccessText(data));
                break;

            case MariaDBParser.RoutineSecurityContext security:
                setSecurityInvoker(security.context?.Type == MariaDBParser.INVOKER);
                break;

            // A COMMENT or LANGUAGE SQL clause does not participate in the model: LANGUAGE
            // SQL is the only language either engine supports, and a comment is not a
            // schema facet Squill tracks.
        }
    }

    // Renders a routine's data-access clause the way information_schema.ROUTINES spells it
    // (e.g. "READS SQL DATA"), so a parsed value compares equal to an extracted one. The
    // context's own text has the keywords concatenated without spaces.
    private static string SqlDataAccessText(MariaDBParser.RoutineDataContext data)
    {
        var words = new List<string>();

        for (var i = 0; i < data.ChildCount; i++)
        {
            if (data.GetChild(i) is ITerminalNode terminal)
            {
                words.Add(terminal.GetText().ToUpperInvariant());
            }
        }

        return string.Join(' ', words);
    }

    // The exact source text a context spans. Unlike GetText(), which concatenates tokens and
    // so discards all whitespace, this reads back from the input stream — required for a
    // routine body, which both engines return verbatim from ROUTINE_DEFINITION.
    private static string SourceText(ParserRuleContext context)
        => context.Start.InputStream.GetText(
            Interval.Of(context.Start.StartIndex, context.Stop.StopIndex));

    private static DataType MapDataType(MariaDBParser.DataTypeContext dataType)
    {
        var (typeName, dimensions, unsigned) = DataTypeDetails(dataType);

        var result = new DataType(typeName.ToLowerInvariant())
        {
            IsUnsigned = unsigned,

            // Read off the context rather than threaded through DataTypeDetails: both apply to
            // several data-type alternatives that the switch below already collapses, and asking
            // the context directly means a new alternative carrying either one is picked up
            // without touching every case.
            IsZerofill = Zerofill(dataType),
            CharacterSet = CharacterSetName(dataType),
        };

        foreach (var dimension in dimensions)
        {
            result.Modifiers.Add(dimension);
        }

        // enum(...) / set(...) carry a list of string-literal values rather than numeric
        // modifiers. They are kept verbatim (quotes and all) so the exact type text can be
        // reproduced when scripting the column.
        if (dataType is MariaDBParser.CollectionDataTypeContext collection
            && collection.collectionOptions() is { } options)
        {
            foreach (var option in options.collectionOption())
            {
                result.CollectionValues.Add(option.STRING_LITERAL().GetText());
            }
        }

        return result;
    }

    // Extracts the canonical type name, any numeric dimensions (length, or precision/scale),
    // and whether the type is UNSIGNED, from a data-type context. Handles the data-type
    // alternatives Squill models; other alternatives fall back to the raw type name text.
    private static (string TypeName, IReadOnlyList<long> Dimensions, bool Unsigned) DataTypeDetails(
        MariaDBParser.DataTypeContext dataType)
    {
        switch (dataType)
        {
            case MariaDBParser.StringDataTypeContext s:
                return (
                    VaryingTypeName(s.typeName.Text, s.VARYING() != null),
                    Dimensions(s.lengthOneDimension()),
                    false);

            case MariaDBParser.DimensionDataTypeContext d:
            {
                var dims = Dimensions(d.lengthOneDimension())
                    .Concat(Dimensions(d.lengthTwoDimension()))
                    .Concat(Dimensions(d.lengthTwoOptionalDimension()))
                    .ToList();
                var unsigned = d.UNSIGNED().Length > 0;
                return (d.typeName.Text, dims, unsigned);
            }

            case MariaDBParser.SimpleDataTypeContext simple:
                return (simple.typeName.Text, Array.Empty<long>(), false);

            case MariaDBParser.NationalStringDataTypeContext n:
                return (n.typeName.Text, Dimensions(n.lengthOneDimension()), false);

            // NATIONAL CHAR VARYING / NATIONAL CHARACTER VARYING (issue #162). The grammar labels
            // this alternative's typeName CHAR or CHARACTER and carries the VARYING separately, so
            // the token alone would name a fixed-width type — it is reported as nvarchar instead,
            // which is what the varying form means. Falling through to the default below would
            // have discarded the length as well, and a national type without its length generates
            // DDL both engines reject.
            case MariaDBParser.NationalVaryingStringDataTypeContext v:
                return ("nvarchar", Dimensions(v.lengthOneDimension()), false);

            // Spatial, collection, long-varchar, uuid, etc.: use the raw text of the type,
            // without modifiers. These are carried verbatim so the DB extractor (which reads
            // the same canonical name) can still hash-match for the simple cases; complex ones
            // are out of the modeled scope.
            default:
                return (FirstTypeToken(dataType), Array.Empty<long>(), false);
        }
    }

    // Whether the type was declared ZEROFILL (issue #190). Only the numeric alternatives accept
    // it, and they all share the dimensionDataType label, so one case answers for all of them.
    private static bool Zerofill(MariaDBParser.DataTypeContext dataType)
        => dataType is MariaDBParser.DimensionDataTypeContext d && d.ZEROFILL().Length > 0;

    // The character set named by a type-level CHARACTER SET, as written (issue #190). Three
    // alternatives accept one and each labels it differently, so each is asked in turn; the
    // spelling is preserved because the deprecated construct *is* a spelling — folding utf8 to
    // utf8mb3 here would erase the very thing being reported on.
    private static string? CharacterSetName(MariaDBParser.DataTypeContext dataType)
        => dataType switch
        {
            MariaDBParser.StringDataTypeContext s => s.charsetName()?.GetText(),
            MariaDBParser.CollectionDataTypeContext c => c.charsetName()?.GetText(),
            MariaDBParser.LongVarcharDataTypeContext l => l.charsetName()?.GetText(),
            _ => null,
        };

    // A trailing VARYING turns a fixed-width character type into its varying counterpart, which
    // the grammar carries as a separate token rather than folding into typeName (issue #162).
    // Measured on both engines: CHAR VARYING(30) and CHARACTER VARYING(30) store as varchar(30),
    // and NCHAR VARYING(30) as a national varchar(30). Reporting the bare typeName here would
    // model a varying column as fixed-width char.
    private static string VaryingTypeName(string typeName, bool isVarying)
    {
        if (!isVarying)
        {
            return typeName;
        }

        return typeName.ToLowerInvariant() switch
        {
            "char" or "character" => "varchar",
            "nchar" => "nvarchar",
            // Every other type the alternative admits (VARCHAR, the TEXT family, LONG) either is
            // already varying or takes no meaning from VARYING, so the written name stands.
            _ => typeName,
        };
    }

    private static string FirstTypeToken(MariaDBParser.DataTypeContext dataType)
    {
        // Fall back to the first terminal token's text as the type name.
        for (var i = 0; i < dataType.ChildCount; i++)
        {
            if (dataType.GetChild(i) is ITerminalNode terminal)
            {
                return terminal.GetText();
            }
        }

        return dataType.GetText();
    }

    private static IReadOnlyList<long> Dimensions(MariaDBParser.LengthOneDimensionContext? context)
        => context is null
            ? Array.Empty<long>()
            : new[] { ParseDecimal(context.decimalLiteral()) };

    private static IReadOnlyList<long> Dimensions(MariaDBParser.LengthTwoDimensionContext? context)
        => context is null
            ? Array.Empty<long>()
            : context.decimalLiteral().Select(ParseDecimal).ToList();

    private static IReadOnlyList<long> Dimensions(MariaDBParser.LengthTwoOptionalDimensionContext? context)
        => context is null
            ? Array.Empty<long>()
            : context.decimalLiteral().Select(ParseDecimal).ToList();

    private static long ParseDecimal(MariaDBParser.DecimalLiteralContext context)
        => long.Parse(context.GetText());

    private static QualifiedName MapQualifiedName(MariaDBParser.FullIdContext fullId)
    {
        // fullId is `uid (DOT_ID | '.' uid)?` — one or two segments. A DOT_ID token carries
        // a leading dot (e.g. ".table") that must be stripped.
        var segments = new List<Identifier> { new(UidText(fullId.uid()[0])) };

        if (fullId.DOT_ID() is { } dotId)
        {
            segments.Add(new Identifier(TrimIdentifier(dotId.GetText().TrimStart('.'))));
        }
        else if (fullId.uid().Length > 1)
        {
            segments.Add(new Identifier(UidText(fullId.uid()[1])));
        }

        return new QualifiedName(segments);
    }

    // The unquoted text of a uid. MariaDB identifiers may be backtick-quoted; strip the
    // backticks and unescape doubled backticks.
    private static string UidText(MariaDBParser.UidContext uid) => TrimIdentifier(uid.GetText());

    private static string TrimIdentifier(string text)
    {
        if (text.Length >= 2 && text[0] == '`' && text[^1] == '`')
        {
            return text[1..^1].Replace("``", "`");
        }

        return text;
    }
}
