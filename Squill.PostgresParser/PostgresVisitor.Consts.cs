using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // sconst : anysconst opt_uescape
    public override SyntaxNode VisitSconst(PostgreSQLParser.SconstContext context)
    {
        var literal = VisitAnysconst(context.anysconst());

        // U&'d!0061t' UESCAPE '!' — the UESCAPE clause names the escape character used inside
        // the preceding unicode literal, so dropping it would change what the string means.
        // Like the literal itself it is carried verbatim.
        if (context.opt_uescape()?.UESCAPE() is null)
        {
            return literal;
        }

        var text = SourceText(context);

        return new LiteralExpression(text, text);
    }

    public override SyntaxNode VisitIconst(PostgreSQLParser.IconstContext context)
    {
        return new LiteralExpression(context.GetText(), long.Parse(context.GetText()));
    }

    public override SyntaxNode VisitFconst(PostgreSQLParser.FconstContext context)
    {
        return new LiteralExpression(context.GetText(), decimal.Parse(context.GetText()));
    }

    public override SyntaxNode VisitAnysconst(PostgreSQLParser.AnysconstContext context)
    {
        if (context.StringConstant() is not null)
        {
            var text = context.GetText();

            if (text[0] != '\'' || text[^1] != '\'')
            {
                throw new PostgresParseException("Expected string literal to start and end with \"'\"");
            }

            var stringValue = text[1..^1].Replace("''", "'");

            return new LiteralExpression(text, stringValue);
        }

        // The remaining forms — U&'d\0061t', E'a\nb', $$text$$ — each have their own escape
        // rules. Squill only needs to reproduce a constant, never to interpret it, so the
        // source spelling is carried verbatim as both text and value: it is already valid SQL
        // and renders back out unchanged. Decoding them here would risk changing the value,
        // which is the one thing a literal must not do.
        //
        // Note the text is taken from the source rather than GetText(), which for a
        // dollar-quoted string concatenates its DollarText tokens without their whitespace.
        var sourceText = SourceText(context);

        return new LiteralExpression(sourceText, sourceText);
    }
}