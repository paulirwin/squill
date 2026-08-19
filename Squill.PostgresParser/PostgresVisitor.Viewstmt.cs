using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // viewstmt
    //   : CREATE (OR REPLACE)? opttemp
    //     ( VIEW qualified_name opt_column_list opt_reloptions
    //     | RECURSIVE VIEW qualified_name OPEN_PAREN columnlist CLOSE_PAREN opt_reloptions )
    //     AS selectstmt opt_check_option
    //
    // Only the facets that make up a view's modeled identity are pulled out — its name, its
    // column list, and the tables it selects from — plus the query text, which is carried
    // verbatim for scripting. The query is deliberately not modeled any more deeply than
    // that: see CreateViewStatement for why a view's body cannot participate in the model.
    public override SyntaxNode VisitViewstmt(PostgreSQLParser.ViewstmtContext context)
    {
        var opttemp = context.opttemp();

        if (opttemp is not null
            && (opttemp.TEMP() is not null || opttemp.TEMPORARY() is not null))
        {
            throw new NotImplementedException(
                "A temporary view is not a persistent schema object and is not supported");
        }

        if (context.RECURSIVE() is not null)
        {
            throw new NotImplementedException("A recursive view is not yet supported");
        }

        if (VisitQualified_name(context.qualified_name()) is not QualifiedName name)
        {
            throw new PostgresParseException("Unable to parse qualified name for view");
        }

        var statement = At(
            new CreateViewStatement(name, context.REPLACE() is not null),
            context);

        foreach (var column in ParseViewColumnList(context.column_list_()))
        {
            statement.ColumnNames.Add(column);
        }

        var select = context.selectstmt();

        statement.Body = SourceText(select);

        foreach (var column in ParseSelectColumns(select))
        {
            statement.SelectColumns.Add(column);
        }

        foreach (var table in ParseSourceTables(select))
        {
            statement.SourceTables.Add(table);
        }

        ApplyViewOptions(statement, context);

        return statement;
    }

    // Issue #208: the clauses that decide how a view executes were parsed and dropped.
    //
    // Both the WITH (...) reloptions and the trailing WITH CHECK OPTION clause are read here
    // because PostgreSQL stores them as one set: measured on 18, `WITH (check_option='local')`
    // and `WITH LOCAL CHECK OPTION` both land in pg_class.reloptions as check_option=local,
    // indistinguishable afterwards. Keeping them as two syntax facets would make one of the
    // two spellings re-diff against its own database on every deploy.
    private static void ApplyViewOptions(
        CreateViewStatement statement, PostgreSQLParser.ViewstmtContext context)
    {
        if (context.reloptions_()?.reloptions()?.reloption_list() is { } reloptionList)
        {
            var options = new List<IndexWithOption>();

            AddStorageParameters(reloptionList, options);

            foreach (var option in options)
            {
                switch (option.Name.ToLowerInvariant())
                {
                    case "check_option":
                        statement.CheckOption = NormalizeCheckOption(option.Value);
                        break;

                    case "security_invoker":
                        statement.SecurityInvoker = ParseReloptionBoolean(option.Value);
                        break;

                    case "security_barrier":
                        statement.SecurityBarrier = ParseReloptionBoolean(option.Value);
                        break;

                    default:
                        // Kept so the model builder can warn rather than let it vanish, which
                        // is the failure issue #208 reported.
                        statement.UnmodeledOptions.Add(option.Name);
                        break;
                }
            }
        }

        // The trailing clause wins over a check_option reloption, matching PostgreSQL: writing
        // both is accepted and the clause is what takes effect.
        if (context.check_option_() is { } checkOption)
        {
            // The rule is `WITH (CASCADED | LOCAL)? CHECK OPTION`, so a bare WITH CHECK OPTION
            // has neither keyword. Measured: PostgreSQL stores that as check_option=cascaded,
            // so it is recorded as CASCADED rather than as a third state.
            statement.CheckOption = checkOption.LOCAL() is not null ? "LOCAL" : "CASCADED";
        }
    }

    // A reloption value is unquoted here so 'local' and local reduce to one token, matching
    // the catalog, which stores the bare word either way.
    private static string NormalizeCheckOption(string? value)
        => value is null ? "CASCADED" : TrimQuotes(value).ToUpperInvariant();

    // A boolean reloption written with no value is true, as PostgreSQL reads it.
    private static bool ParseReloptionBoolean(string? value)
        => value is null
            || TrimQuotes(value).ToLowerInvariant() is "true" or "on" or "yes" or "1";

    private static string TrimQuotes(string value)
        => value.Length >= 2 && value[0] == '\'' && value[^1] == '\''
            ? value[1..^1]
            : value;

    private IEnumerable<Identifier> ParseViewColumnList(
        PostgreSQLParser.Column_list_Context? context)
    {
        var columnList = context?.columnlist();

        if (columnList is null)
        {
            yield break;
        }

        foreach (var column in columnList.columnElem())
        {
            if (VisitColid(column.colid()) is not Identifier identifier)
            {
                throw new NotImplementedException("Unable to parse a view column name");
            }

            yield return identifier;
        }
    }

    // Reduces the select list to one entry per view column. A set operation (UNION, …) takes
    // its column names from the first branch, which is also how PostgreSQL names them.
    private IEnumerable<ViewSelectColumn> ParseSelectColumns(PostgreSQLParser.SelectstmtContext select)
    {
        var simple = FirstSimpleSelect(select);

        if (simple is null)
        {
            throw new NotImplementedException(
                "A view over this form of query is not yet supported; "
                + "only a SELECT with an explicit target list is modeled");
        }

        var targetList = simple.target_list_()?.target_list()
            ?? simple.target_list();

        if (targetList is null)
        {
            throw new NotImplementedException(
                "A view over a query without a target list is not supported");
        }

        foreach (var target in targetList.target_el())
        {
            yield return ParseTargetElement(target);
        }
    }

    private ViewSelectColumn ParseTargetElement(PostgreSQLParser.Target_elContext target)
    {
        // target_el : a_expr (AS colLabel | bareColLabel |) # target_label
        //           | STAR                                 # target_star
        if (target is PostgreSQLParser.Target_starContext)
        {
            return ViewSelectColumn.Wildcard();
        }

        if (target is not PostgreSQLParser.Target_labelContext label)
        {
            return ViewSelectColumn.Unnamed();
        }

        // An explicit alias always wins, whatever the expression is.
        if (label.colLabel() is { } collabel)
        {
            return ViewSelectColumn.Aliased(ParseCollabel(collabel));
        }

        // The bare (no AS) alias form. Like colLabel it is either a real identifier or a
        // keyword usable as a label.
        if (label.bareColLabel() is { } bareLabel)
        {
            return ViewSelectColumn.Aliased(
                bareLabel.identifier() is { } identifier
                    ? ParseIdentifierName(identifier)
                    : bareLabel.GetText());
        }

        return ParseUnaliasedTarget(label.a_expr());
    }

    private string ParseCollabel(PostgreSQLParser.ColLabelContext collabel)
        => collabel.identifier() is { } identifier
            ? ParseIdentifierName(identifier)
            // A keyword used as a label carries no quoting to strip.
            : collabel.GetText();

    private string ParseIdentifierName(PostgreSQLParser.IdentifierContext context)
        => VisitIdentifier(context) is Identifier identifier
            ? identifier.Name
            : throw new NotImplementedException("Unable to parse a view column alias");

    // Without an alias, only a bare column reference names a column: `id`, `users.id`, or a
    // qualified wildcard `u.*`. Anything else (an expression, a function call) has no name
    // Squill can derive, and becomes an error at model-building time.
    private ViewSelectColumn ParseUnaliasedTarget(PostgreSQLParser.A_exprContext? expression)
    {
        if (expression is null)
        {
            return ViewSelectColumn.Unnamed();
        }

        var columnref = FindColumnRef(expression);

        if (columnref is null)
        {
            return ViewSelectColumn.Unnamed();
        }

        if (VisitColid(columnref.colid()) is not Identifier colidIdentifier)
        {
            return ViewSelectColumn.Unnamed();
        }

        var name = colidIdentifier.Name;
        var indirection = columnref.indirection();

        if (indirection is null)
        {
            return ViewSelectColumn.Named(name);
        }

        var elements = indirection.indirection_el();

        // `t.*` is a wildcard qualified by the table; `t.col` names the bare column. Deeper
        // indirection (composite-field access) is not a plain column reference.
        if (elements.Length != 1)
        {
            return ViewSelectColumn.Unnamed();
        }

        var element = elements[0];

        if (element.STAR() is not null)
        {
            return ViewSelectColumn.Wildcard(name);
        }

        if (element.attr_name() is { } attribute)
        {
            return ViewSelectColumn.Named(ParseCollabel(attribute.colLabel()), name);
        }

        return ViewSelectColumn.Unnamed();
    }

    // Walks down to the columnref an expression consists of, if that is all it is. A
    // single-child chain is transparent (the grammar layers a_expr through many levels of
    // precedence); a node with several children is a real expression, not a column.
    private static PostgreSQLParser.ColumnrefContext? FindColumnRef(IParseTree node)
    {
        while (true)
        {
            switch (node)
            {
                case PostgreSQLParser.ColumnrefContext columnref:
                    return columnref;

                case { ChildCount: 1 }:
                    node = node.GetChild(0);
                    continue;

                default:
                    return null;
            }
        }
    }

    private IEnumerable<QualifiedName> ParseSourceTables(PostgreSQLParser.SelectstmtContext select)
    {
        var simple = FirstSimpleSelect(select);

        var fromClause = simple?.from_clause();

        if (fromClause?.from_list() is not { } fromList)
        {
            yield break;
        }

        foreach (var tableRef in fromList.table_ref())
        {
            // Only a plain table reference contributes a resolvable name; a subquery, a
            // function call or a join tree does not name a table Squill can look up.
            if (tableRef.relation_expr() is not { } relation)
            {
                continue;
            }

            if (VisitQualified_name(relation.qualified_name()) is QualifiedName qualifiedName)
            {
                yield return qualifiedName;
            }
        }
    }

    // The first simple_select in a (possibly parenthesized, possibly set-operation) query.
    private static PostgreSQLParser.Simple_select_pramaryContext? FirstSimpleSelect(IParseTree node)
    {
        switch (node)
        {
            case PostgreSQLParser.Simple_select_pramaryContext simple:
                return simple;

            // A CTE's inner query must not be mistaken for the outer select list.
            case PostgreSQLParser.With_clauseContext:
                return null;
        }

        for (var i = 0; i < node.ChildCount; i++)
        {
            if (FirstSimpleSelect(node.GetChild(i)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // The exact source text a context spans. GetText() concatenates tokens and so discards
    // all whitespace; a view body is kept as written.
    private static string SourceText(ParserRuleContext context)
        => context.Start.InputStream.GetText(
            Interval.Of(context.Start.StartIndex, context.Stop.StopIndex));
}
