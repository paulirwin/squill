using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    /// <summary>
    /// Maps the <c>func_expr_common_subexpr</c> grammar rule (issue #140) — the alternative of
    /// <c>func_expr</c> that is not a plain <c>func_application</c>.
    ///
    /// Alternatives that have the shape of an ordinary call (a bare comma-separated argument
    /// list: <c>COALESCE</c>, <c>NULLIF</c>, <c>GREATEST</c>, <c>LEAST</c>, <c>XMLCONCAT</c>)
    /// reuse <see cref="FunctionApplicationExpression"/> so they render and model like any
    /// other call. The rest get their own node, because their operands are separated by
    /// keywords (<c>EXTRACT ... FROM</c>, <c>SUBSTRING ... FROM ... FOR</c>,
    /// <c>POSITION ... IN</c>) or they take no parentheses at all (<c>CURRENT_TIMESTAMP</c>) —
    /// forcing those into an argument list would lose the spelling needed to render them back.
    /// </summary>
    public override SyntaxNode VisitFunc_expr_common_subexpr(
        PostgreSQLParser.Func_expr_common_subexprContext context)
    {
        // The niladic keywords. CURRENT_TIME / CURRENT_TIMESTAMP / LOCALTIME / LOCALTIMESTAMP
        // additionally accept a fractional-seconds precision.
        if (context.CURRENT_DATE() is not null) return Keyword(context, "CURRENT_DATE");
        if (context.CURRENT_TIME() is not null) return Keyword(context, "CURRENT_TIME");
        if (context.CURRENT_TIMESTAMP() is not null) return Keyword(context, "CURRENT_TIMESTAMP");
        if (context.LOCALTIME() is not null) return Keyword(context, "LOCALTIME");
        if (context.LOCALTIMESTAMP() is not null) return Keyword(context, "LOCALTIMESTAMP");
        if (context.CURRENT_ROLE() is not null) return Keyword(context, "CURRENT_ROLE");
        if (context.CURRENT_USER() is not null) return Keyword(context, "CURRENT_USER");
        if (context.SESSION_USER() is not null) return Keyword(context, "SESSION_USER");
        if (context.USER() is not null) return Keyword(context, "USER");
        if (context.CURRENT_CATALOG() is not null) return Keyword(context, "CURRENT_CATALOG");
        if (context.CURRENT_SCHEMA() is not null) return Keyword(context, "CURRENT_SCHEMA");

        if (context.COLLATION() is not null)
        {
            return new CollationForExpression(RequireExpression(context.a_expr(0)));
        }

        // CAST and TREAT share a shape: a single a_expr, AS, a typename.
        if (context.CAST() is not null || context.TREAT() is not null)
        {
            return new CastExpression(
                RequireExpression(context.a_expr(0)),
                RequireDataType(context.typename()),
                isTreat: context.TREAT() is not null);
        }

        if (context.EXTRACT() is not null)
        {
            return VisitExtract(context.extract_list());
        }

        if (context.NORMALIZE() is not null)
        {
            return new NormalizeExpression(
                RequireExpression(context.a_expr(0)),
                context.unicode_normal_form()?.GetText().ToUpperInvariant());
        }

        if (context.OVERLAY() is not null)
        {
            return VisitOverlay(context.overlay_list());
        }

        if (context.POSITION() is not null)
        {
            return VisitPosition(context.position_list());
        }

        if (context.SUBSTRING() is not null)
        {
            return VisitSubstring(context.substr_list());
        }

        if (context.TRIM() is not null)
        {
            return VisitTrim(context);
        }

        // The alternatives whose arguments are a plain comma list behave like an ordinary
        // call, so they reuse FunctionApplicationExpression.
        if (context.NULLIF() is not null)
        {
            return CallOf("NULLIF", RequireExpression(context.a_expr(0)),
                RequireExpression(context.a_expr(1)));
        }

        if (context.COALESCE() is not null) return CallOf("COALESCE", context.expr_list());
        if (context.GREATEST() is not null) return CallOf("GREATEST", context.expr_list());
        if (context.LEAST() is not null) return CallOf("LEAST", context.expr_list());
        if (context.XMLCONCAT() is not null) return CallOf("XMLCONCAT", context.expr_list());

        // The remaining XML constructors (XMLELEMENT, XMLPARSE, XMLROOT, …) each have their
        // own bespoke syntax and no modeled equivalent yet; the issue calls for landing them
        // last. Everything above is reachable, so this arm is only the XML tail.
        throw new NotImplementedException(
            $"Support for the '{context.GetChild(0).GetText().ToUpperInvariant()}' expression "
            + "is not yet implemented");
    }

    // A niladic keyword, plus the optional fractional-seconds precision the time keywords take.
    private static KeywordExpression Keyword(
        PostgreSQLParser.Func_expr_common_subexprContext context, string keyword)
    {
        int? precision = context.iconst() is { } iconst
            ? int.Parse(iconst.GetText())
            : null;

        return new KeywordExpression(keyword, precision);
    }

    private ExtractExpression VisitExtract(PostgreSQLParser.Extract_listContext? context)
    {
        // extract_list is nullable in the grammar (`extract_arg FROM a_expr |`), but an
        // EXTRACT with no arguments is not valid Postgres.
        if (context?.extract_arg() is not { } arg)
        {
            throw new PostgresParseException("EXTRACT requires a field and a source expression");
        }

        return new ExtractExpression(arg.GetText(), RequireExpression(context.a_expr()));
    }

    private OverlayExpression VisitOverlay(PostgreSQLParser.Overlay_listContext context)
    {
        // overlay_list : a_expr PLACING a_expr FROM a_expr (FOR a_expr)?
        var expressions = context.a_expr();

        return new OverlayExpression(
            RequireExpression(expressions[0]),
            RequireExpression(expressions[1]),
            RequireExpression(expressions[2]),
            expressions.Length > 3 ? RequireExpression(expressions[3]) : null);
    }

    private PositionExpression VisitPosition(PostgreSQLParser.Position_listContext? context)
    {
        // position_list : b_expr IN_P b_expr |  — the empty alternative is not valid Postgres.
        if (context?.b_expr() is not { Length: 2 } operands)
        {
            throw new PostgresParseException("POSITION requires a substring and a source expression");
        }

        return new PositionExpression(
            RequireExpression(operands[0]),
            RequireExpression(operands[1]));
    }

    private Expression VisitSubstring(PostgreSQLParser.Substr_listContext context)
    {
        // The plain comma form SUBSTRING(s, start, count) is an ordinary call.
        if (context.expr_list() is { } exprList)
        {
            return CallOf("SUBSTRING", exprList);
        }

        var expressions = context.a_expr();
        var substring = new SubstringExpression(RequireExpression(expressions[0]));

        // SUBSTRING(s SIMILAR pattern ESCAPE escape)
        if (context.SIMILAR() is not null)
        {
            substring.Similar = RequireExpression(expressions[1]);
            substring.Escape = RequireExpression(expressions[2]);
            return substring;
        }

        // The FROM and FOR parts may appear in either order, and either may be absent, so
        // walk the children rather than relying on a_expr() positions.
        var next = 1;

        foreach (var child in context.children)
        {
            if (child == context.FROM())
            {
                substring.From = RequireExpression(expressions[next++]);
            }
            else if (child == context.FOR())
            {
                substring.For = RequireExpression(expressions[next++]);
            }
        }

        return substring;
    }

    private TrimExpression VisitTrim(PostgreSQLParser.Func_expr_common_subexprContext context)
    {
        var side = context.LEADING() is not null ? TrimSide.Leading
            : context.TRAILING() is not null ? TrimSide.Trailing
            : TrimSide.Both;

        var trimList = context.trim_list();

        // trim_list : a_expr FROM expr_list | FROM expr_list | expr_list
        // The leading a_expr, when present, is the set of characters to trim.
        var characters = trimList.a_expr() is { } charactersExpr
            ? RequireExpression(charactersExpr)
            : null;

        var sources = trimList.expr_list().a_expr()
            .Select(RequireExpression)
            .ToList<Expression>();

        return new TrimExpression(side, characters, sources);
    }

    private FunctionApplicationExpression CallOf(string name, PostgreSQLParser.Expr_listContext context)
        => CallOf(name, context.a_expr().Select(RequireExpression).ToArray());

    private static FunctionApplicationExpression CallOf(string name, params Expression[] arguments)
    {
        var function = new FunctionApplicationExpression(name);

        foreach (var argument in arguments)
        {
            function.Arguments.Add(new FunctionArgument(argument));
        }

        return function;
    }

    private Expression RequireExpression(PostgreSQLParser.A_exprContext context)
        => VisitA_expr(context) as Expression
           ?? throw new PostgresParseException("Unable to parse expression");

    private Expression RequireExpression(PostgreSQLParser.B_exprContext context)
        => VisitB_expr(context) as Expression
           ?? throw new PostgresParseException("Unable to parse expression");

    private DataType RequireDataType(PostgreSQLParser.TypenameContext context)
        => VisitTypename(context) as DataType
           ?? throw new PostgresParseException("Unable to parse data type");
}
