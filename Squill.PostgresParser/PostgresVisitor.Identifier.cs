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
            // Taken from the token rather than GetText(), which would also pull in the
            // trailing UESCAPE clause the grammar now admits after a unicode-quoted
            // identifier (`U&"d!0061t" UESCAPE '!'`).
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

        // A PL/pgSQL variable reference (`:name`). The grammar spells this as a bare token
        // on `identifier` rather than routing through the `plsqlvariablename` rule, so the
        // token is read directly; the leading colon is not part of the name.
        if (context.PLSQLVARIABLENAME() is { } plsqlVariableName)
        {
            return new PLSQLVariableName(plsqlVariableName.GetText().TrimStart(':'));
        }

        throw new NotImplementedException(
            "Support for quoted identifiers and other identifier types not yet implemented");
    }
}