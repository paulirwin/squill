using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitIdentifier(PostgreSQLParser.IdentifierContext context)
    {
        if (context.Identifier() is { } identifierName)
        {
            return new SimpleIdentifier(identifierName.GetText());
        }

        var unicodeQuoted = context.UnicodeQuotedIdentifier();

        if (context.QuotedIdentifier() is not null
            || unicodeQuoted is not null)
        {
            // Taken from the token rather than GetText(), which would also pull in a
            // trailing UESCAPE clause. (In practice the grammar rejects `UESCAPE` after an
            // identifier before the visitor is reached, so this is belt-and-braces.)
            string text = (context.QuotedIdentifier() ?? unicodeQuoted!).GetText();

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