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
            // trailing UESCAPE clause the grammar admits after a unicode-quoted identifier
            // (`U&"d!0061t" UESCAPE '!'`).
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

            // An identifier is not a literal: PostgreSQL DECODES a unicode escape before
            // storing the name, where it stores a string constant exactly as written.
            // Measured against postgres:latest — both `U&"d\0061t"` and
            // `U&"d!0061t" UESCAPE '!'` create a table named `dat`. Carrying the raw text the
            // way the string-constant path does would put a name in the model that the engine
            // never creates, so the object would re-diff on every deploy (or the generated DDL
            // would create a differently-named table).
            //
            // Decoding is the real fix and is not attempted here: the escape character is
            // redeclarable, surrogate pairs are permitted, and getting it subtly wrong would
            // corrupt names silently. Until then this rejects what it cannot represent, which
            // is what the rest of the visitor does with a construct it does not model.
            if (unicodeQuoted is not null && ContainsUnicodeEscape(name, context))
            {
                throw new NotImplementedException(
                    "Support for escape sequences in a unicode-quoted identifier (U&\"...\", "
                    + "with or without UESCAPE) is not yet implemented. PostgreSQL decodes "
                    + "these into the stored name, so carrying the source spelling would name "
                    + "the object something the server never creates. Spell the identifier "
                    + "with the character itself, or as a plain quoted identifier.");
            }

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

    // Whether a unicode-quoted identifier's body carries an escape sequence that PostgreSQL
    // would decode. The escape character is the backslash unless a UESCAPE clause redeclares
    // it, so the clause has to be read to know what to look for.
    //
    // A doubled escape character is the escape itself, not the start of a sequence
    // (`U&"a\\b"` is the four-character name `a\b`), so those are skipped in pairs rather
    // than counted.
    private static bool ContainsUnicodeEscape(string name, PostgreSQLParser.IdentifierContext context)
    {
        var escape = UescapeCharacter(context);

        for (var i = 0; i < name.Length; i++)
        {
            if (name[i] != escape)
            {
                continue;
            }

            // A doubled escape stands for one literal escape character: step over both.
            if (i + 1 < name.Length && name[i + 1] == escape)
            {
                i++;
                continue;
            }

            return true;
        }

        return false;
    }

    // The escape character declared by a trailing UESCAPE clause, or the backslash default.
    // The clause's operand is a single-character string constant (`UESCAPE '!'`).
    private static char UescapeCharacter(PostgreSQLParser.IdentifierContext context)
    {
        var text = context.uescape_()?.anysconst()?.GetText();

        if (text is not { Length: >= 3 })
        {
            return '\\';
        }

        // Strip the surrounding quotes; what remains is the escape character.
        return text[1];
    }
}