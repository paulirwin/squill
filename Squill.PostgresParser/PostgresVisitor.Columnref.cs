using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitColumnref(PostgreSQLParser.ColumnrefContext context)
    {
        if (VisitColid(context.colid()) is not Identifier identifier)
        {
            throw new PostgresParseException("Unable to parse column reference identifier");
        }

        return new ColumnReferenceExpression(identifier);
    }
}