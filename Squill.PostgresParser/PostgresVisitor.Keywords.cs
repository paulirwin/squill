using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitUnreserved_keyword(PostgreSQLParser.Unreserved_keywordContext context)
    {
        // TODO: do unreserved keywords deserve their own type?
        return new SimpleIdentifier(context.GetText());
    }

    public override SyntaxNode VisitCol_name_keyword(PostgreSQLParser.Col_name_keywordContext context)
    {
        // TODO: do col name keywords deserve their own type?
        return new SimpleIdentifier(context.GetText());
    }
}