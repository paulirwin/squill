using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitSconst(PostgreSQLParser.SconstContext context)
    {
        if (context.opt_uescape() is not null && context.opt_uescape().UESCAPE() is not null)
        {
            throw new NotImplementedException("UESCAPE not yet supported");
        }

        return VisitAnysconst(context.anysconst());
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

        throw new NotImplementedException("Support for other string constant types not yet implemented");
    }
}