using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // aexprconst
    //   : iconst | fconst | sconst | bconst | xconst
    //   | func_name (sconst | OPEN_PAREN func_arg_list opt_sort_clause CLOSE_PAREN sconst)
    //   | consttypename sconst
    //   | constinterval (sconst opt_interval | OPEN_PAREN iconst CLOSE_PAREN sconst)
    //   | TRUE_P | FALSE_P | NULL_P
    //   ;
    public override SyntaxNode VisitAexprconst(PostgreSQLParser.AexprconstContext context)
    {
        if (context.iconst() is { } iconst)
        {
            return VisitIconst(iconst);
        }

        if (context.fconst() is { } fconst)
        {
            return VisitFconst(fconst);
        }

        if (context.TRUE_P() is not null)
        {
            return new LiteralExpression(context.GetText(), true);
        }

        if (context.FALSE_P() is not null)
        {
            return new LiteralExpression(context.GetText(), false);
        }

        // NULL is rendered back out as the keyword, so the text is the value; there is no
        // CLR value that would round-trip it.
        if (context.NULL_P() is not null)
        {
            return new LiteralExpression("NULL", "NULL");
        }

        // A bit-string (B'101') or hexadecimal (X'1f') constant. Both are carried verbatim —
        // the source spelling is already valid SQL and needs no interpretation.
        if (context.bconst() is { } bconst)
        {
            return new LiteralExpression(bconst.GetText(), bconst.GetText());
        }

        if (context.xconst() is { } xconst)
        {
            return new LiteralExpression(xconst.GetText(), xconst.GetText());
        }

        if (context.sconst() is not { } sconst)
        {
            // What remains is the func_name '...' form, whose type is an arbitrary
            // (possibly user-defined) name; modeling it needs more than a verbatim copy.
            throw new NotImplementedException("Aexprconst alternate not yet supported");
        }

        if (VisitSconst(sconst) is not LiteralExpression literal)
        {
            throw new PostgresParseException("Unable to parse string constant");
        }

        // `interval '1 day'`, with an optional trailing qualifier (`interval '1' DAY`).
        if (context.constinterval() is { } constinterval)
        {
            var modifier = context.interval_();

            // opt_interval also matches empty, which is the common case (`interval '1 day'`
            // carries its units inside the literal). GetText() is the reliable emptiness
            // test here; the span is only taken from the source once there is one.
            var hasModifier = !string.IsNullOrEmpty(modifier?.GetText());

            return new TypedLiteralExpression(
                SourceText(constinterval),
                literal,
                hasModifier ? SourceText(modifier!) : null);
        }

        // `timestamp '2020-01-01'` and friends. The type prefix changes the constant's
        // meaning, so it is carried rather than dropped.
        //
        // Taken from the source rather than GetText(), which concatenates tokens without
        // their whitespace and would flatten `timestamp with time zone` into one word.
        if (context.consttypename() is { } consttypename)
        {
            return new TypedLiteralExpression(SourceText(consttypename), literal);
        }

        // A plain string constant.
        return literal;
    }
}
