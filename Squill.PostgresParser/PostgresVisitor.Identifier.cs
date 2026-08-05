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

            // An identifier is not a literal: PostgreSQL DECODES a unicode-quoted identifier
            // before storing the name, where it stores a string constant exactly as written.
            // Measured against postgres:latest — both `U&"d\0061t"` and
            // `U&"d!0061t" UESCAPE '!'` create a table named `dat`. Carrying the raw text the
            // way the string-constant path does would put a name in the model that the engine
            // never creates, so the object would re-diff on every deploy (or the generated DDL
            // would create a differently-named table).
            //
            // Two of the three decodings are handled here. A `\XXXX` sequence is rejected:
            // surrogate pairs and the server's encoding both bear on the result, and getting
            // it subtly wrong would corrupt names silently. A doubled escape is collapsed,
            // because that one is unambiguous — `U&"a\\b"` is the three-character name `a\b`,
            // measured — and rejecting it would refuse an identifier Squill can represent
            // exactly.
            if (unicodeQuoted is not null)
            {
                var escape = UescapeCharacter(context);

                if (ContainsUnicodeEscape(name, escape))
                {
                    throw new NotImplementedException(
                        "Support for escape sequences in a unicode-quoted identifier (U&\"...\", "
                        + "with or without UESCAPE) is not yet implemented. PostgreSQL decodes "
                        + "these into the stored name, so carrying the source spelling would name "
                        + "the object something the server never creates. Spell the identifier "
                        + "with the character itself, or as a plain quoted identifier.");
                }

                name = CollapseDoubledEscapes(name, escape);
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

    // Whether a unicode-quoted identifier's body carries an escape SEQUENCE (`\XXXX`), which is
    // the part PostgreSQL decodes and this does not reproduce.
    //
    // A doubled escape character is not one: it stands for a single literal escape character
    // and is collapsed rather than rejected, so those are stepped over in pairs.
    private static bool ContainsUnicodeEscape(string name, char escape)
    {
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

    // Collapses each doubled escape character to one, which is what PostgreSQL stores:
    // `U&"a\\b"` is the three-character name `a\b` (measured). Only reached once
    // ContainsUnicodeEscape has ruled out a real escape sequence, so every occurrence of the
    // escape character here is part of a doubled pair.
    private static string CollapseDoubledEscapes(string name, char escape)
    {
        var doubled = $"{escape}{escape}";

        return name.Contains(doubled, StringComparison.Ordinal)
            ? name.Replace(doubled, escape.ToString(), StringComparison.Ordinal)
            : name;
    }

    // The escape character declared by a trailing UESCAPE clause, or the backslash default.
    //
    // PostgreSQL accepts any string-constant spelling for the operand — `UESCAPE E'!'` and
    // `UESCAPE $$!$$` both declare `!` (measured) — but only the plain single-quoted form is
    // read here. The others are rejected rather than guessed at: taking the second character of
    // `E'!'` would yield `'`, silently applying the WRONG escape character and so mis-deciding
    // which identifiers carry a sequence. A construct this cannot read is refused, not assumed.
    private static char UescapeCharacter(PostgreSQLParser.IdentifierContext context)
    {
        var text = context.uescape_()?.anysconst()?.GetText();

        if (text is null)
        {
            return '\\';
        }

        // The plain form is exactly quote, one character, quote. PostgreSQL rejects a
        // multi-character escape itself ("invalid Unicode escape character"), so anything
        // else here is either a different literal spelling or invalid SQL.
        if (text is not { Length: 3 } || text[0] != '\'' || text[2] != '\'')
        {
            throw new NotImplementedException(
                $"Support for a UESCAPE escape character spelled as '{text}' is not yet "
                + "implemented; only the plain form (UESCAPE '!') is read. PostgreSQL decodes "
                + "the identifier using this character, so reading it wrongly would name the "
                + "object something the server never creates.");
        }

        return text[1];
    }
}