using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitIdentifier(PostgreSQLParser.IdentifierContext context)
    {
        if (context.Identifier() is { } identifierName)
        {
            if (context.opt_uescape()?.UESCAPE() is not null)
            {
                throw new NotImplementedException("Support for UESCAPE not yet implemented");
            }

            return new SimpleIdentifier(identifierName.GetText());
        }

        var unicodeQuoted = context.UnicodeQuotedIdentifier();

        if (context.QuotedIdentifier() is not null
            || unicodeQuoted is not null)
        {
            string text = context.GetText();

            if (text.StartsWith("U&"))
            {
                text = text[2..];
            }

            if (text[0] != '"' || text[^1] != '"')
            {
                throw new NotImplementedException("Unable to parse quoted identifier");
            }

            string name = text[1..^1];

            return new SimpleIdentifier(name, isQuoted: true, isUnicodeQuoted: unicodeQuoted is not null);
        }

        if (context.plsqlvariablename() is { } plsqlvariablename)
        {
            return VisitPlsqlvariablename(plsqlvariablename);
        }

        if (context.plsql_unreserved_keyword() is { } plsqlUnreservedKeyword)
        {
            return VisitPlsql_unreserved_keyword(plsqlUnreservedKeyword);
        }

        throw new NotImplementedException(
            "Support for quoted identifiers and other identifier types not yet implemented");
    }
}