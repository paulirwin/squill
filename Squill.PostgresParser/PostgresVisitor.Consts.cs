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
        if (context.uescape_()?.UESCAPE() is null)
        {
            return literal;
        }

        var text = SourceText(context);

        return new LiteralExpression(text, text);
    }

    // iconst : Integral | BinaryIntegral | OctalIntegral | HexadecimalIntegral
    //
    // The three prefixed forms are PostgreSQL 16's non-decimal integer literals (issue #191).
    // The value is decoded from the appropriate radix, but the text keeps the spelling the
    // author wrote: the renderer emits Text, so 0x19 deploys as 0x19 rather than being rewritten
    // to 25. Normalizing it would change the source for no reason and re-diff against a database
    // that stored the original spelling.
    //
    // Note the grammar's Digits fragment is [0-9]+, so the prefixed tokens match decimal digits
    // regardless of radix — 0b999 and 0o99 lex as literals even though neither is one. Those are
    // rejected here rather than reinterpreted, because quietly resolving 0b999 to some other
    // number would deploy a value the source never named.
    public override SyntaxNode VisitIconst(PostgreSQLParser.IconstContext context)
    {
        var text = context.GetText();

        var radix = true switch
        {
            _ when context.BinaryIntegral() is not null => IntegerLiteralRadix.Binary,
            _ when context.OctalIntegral() is not null => IntegerLiteralRadix.Octal,
            _ when context.HexadecimalIntegral() is not null => IntegerLiteralRadix.Hexadecimal,
            _ => IntegerLiteralRadix.Decimal,
        };

        try
        {
            if (radix == IntegerLiteralRadix.Decimal)
            {
                return new LiteralExpression(text, long.Parse(text));
            }

            var fromBase = radix switch
            {
                IntegerLiteralRadix.Binary => 2,
                IntegerLiteralRadix.Octal => 8,
                _ => 16,
            };

            // The '0x'/'0o'/'0b' prefix is the parser's marker for the radix, not part of the
            // digits, so it is stripped before conversion.
            var digits = text[2..];

            // Convert.ToInt64 does NOT range-check for these bases: it reinterprets an
            // out-of-range string as two's complement, so 0x9999999999999999 would come back as
            // -7378697629483820647 rather than throwing. That is not merely a different number,
            // it is a different sign, and it is not what the engine does either — measured on 16,
            // PostgreSQL reports that literal as the positive 11068046444225730969, promoting
            // past bigint rather than wrapping. Parsing unsigned first makes the range check real,
            // and keeps the decimal and non-decimal paths agreeing about what is too large.
            var unsigned = Convert.ToUInt64(digits, fromBase);

            if (unsigned > long.MaxValue)
            {
                throw new OverflowException(
                    $"Value {unsigned} is too large to be represented as a 64-bit integer.");
            }

            return new LiteralExpression(text, (long)unsigned, radix);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            // A PostgresParseException rather than the raw framework exception: this is a defect
            // in the author's SQL, and only a parse exception carries far enough for the build to
            // report it against the source rather than as a stack trace.
            throw new PostgresParseException(
                $"Invalid integer literal '{text}'",
                context.Start.Line,
                context.Start.Column,
                ex);
        }
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