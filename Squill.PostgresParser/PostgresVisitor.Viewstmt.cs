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

        foreach (var column in ParseViewColumnList(context.opt_column_list()))
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

        return statement;
    }

    private IEnumerable<Identifier> ParseViewColumnList(
        PostgreSQLParser.Opt_column_listContext? context)
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

        var targetList = simple.opt_target_list()?.target_list()
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
        // target_el : a_expr (AS collabel | identifier |) # target_label
        //           | STAR                               # target_star
        if (target is PostgreSQLParser.Target_starContext)
        {
            return ViewSelectColumn.Wildcard();
        }

        if (target is not PostgreSQLParser.Target_labelContext label)
        {
            return ViewSelectColumn.Unnamed();
        }

        // An explicit alias always wins, whatever the expression is.
        if (label.collabel() is { } collabel)
        {
            return ViewSelectColumn.Aliased(ParseCollabel(collabel));
        }

        if (label.identifier() is { } identifier)
        {
            return ViewSelectColumn.Aliased(ParseIdentifierName(identifier));
        }

        return ParseUnaliasedTarget(label.a_expr());
    }

    private string ParseCollabel(PostgreSQLParser.CollabelContext collabel)
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
            return ViewSelectColumn.Named(ParseCollabel(attribute.collabel()), name);
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
    private static PostgreSQLParser.Simple_selectContext? FirstSimpleSelect(IParseTree node)
    {
        switch (node)
        {
            case PostgreSQLParser.Simple_selectContext simple:
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
