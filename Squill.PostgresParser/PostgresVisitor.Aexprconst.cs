using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitAexprconst(PostgreSQLParser.AexprconstContext context)
    {
        if (context.sconst() is { } sconst)
        {
            return VisitSconst(sconst);
        }

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

        throw new NotImplementedException("Aexprconst alternate not yet supported");
    }
}