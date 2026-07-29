using System.Text;
using Antlr4.Runtime;

namespace Squill.MariaDbParser;

/// <summary>
/// Reduces a CHECK predicate or generated-column expression to a canonical token, so the text as
/// DECLARED in source and the text as EXTRACTED from a live database compare equal (issue #156).
///
/// This is what lets those expressions take part in their element's identity. Without it the
/// property has to be excluded from the Merkle hash, and redefining a predicate under the same
/// name changes no hash, produces no delta, and is silently ignored while the old predicate stays
/// enforced.
///
/// Unlike PostgreSQL — which rewrites an expression's structure, and so needs a normalizing pass
/// over a parsed tree — MariaDB and MySQL rewrite only the surface tokens. Measured against live
/// servers, <c>price &gt; 0</c> is reported as <c>`price` &gt; 0</c> by MariaDB and
/// <c>(`price` &gt; 0)</c> by MySQL, with the structure intact in both. So normalizing token by
/// token is sufficient here, and this works directly on text rather than needing an expression
/// tree the MariaDB parser does not build.
///
/// The differences it reconciles:
/// <list type="bullet">
/// <item>backtick-quoted identifiers, which are unquoted (their case is significant and is
///   preserved);</item>
/// <item>keywords and operators, which the engines lower-case and which are folded to upper;</item>
/// <item>the parentheses MySQL wraps around the whole predicate, and any other redundant
///   grouping;</item>
/// <item>MySQL's charset introducer on a string literal (<c>_latin1\'a%\'</c>), which names how
///   the literal is interpreted rather than what the predicate tests;</item>
/// <item>whitespace, which is re-spaced around operators and after commas.</item>
/// </list>
///
/// Text that cannot be tokenized makes <see cref="TryNormalize"/> return <c>false</c> rather than
/// emit a guess. A wrong canonical form is worse than none: it makes an unchanged predicate look
/// changed and redeploys the object on every deploy, whereas no canonical form merely leaves the
/// property out of the hash — the known gap this class narrows.
/// </summary>
public static class ExpressionNormalizer
{
    /// <summary>
    /// Produces the canonical form of <paramref name="expression"/>, or returns <c>false</c> when
    /// it cannot be tokenized.
    /// </summary>
    public static bool TryNormalize(string expression, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(expression))
        {
            return false;
        }

        var tokens = Tokenize(Unescape(expression));

        if (tokens is null || tokens.Count == 0)
        {
            return false;
        }

        canonical = Render(StripRedundantParentheses(tokens));
        return true;
    }

    // MySQL reports a CHECK clause with its quotes backslash-escaped — `_latin1\'a%\'` — which is
    // an artifact of how information_schema renders the clause, not part of the predicate. The
    // lexer cannot make sense of it (the backslash comes back as an error token), so the escaping
    // is undone before lexing rather than handled token by token.
    private static string Unescape(string expression)
        => expression.Replace("\\'", "'").Replace("\\\"", "\"");

    // Lexes the expression, collecting the tokens Squill needs to compare. Returns null when the
    // lexer reports an error, so malformed text is refused rather than silently half-normalized.
    private static List<IToken>? Tokenize(string expression)
    {
        var lexer = new MariaDBLexer(new AntlrInputStream(expression));
        var errors = new SyntaxErrorCollectingListener();

        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);

        var stream = new CommonTokenStream(lexer);
        stream.Fill();

        if (errors.Errors.Count > 0)
        {
            return null;
        }

        var tokens = stream.GetTokens()
            .Where(token => token.Type != TokenConstants.EOF
                && token.Channel == TokenConstants.DefaultChannel)
            .ToList();

        // The lexer does not report an unterminated literal as an error — it degrades, emitting
        // the opening quote as a bare symbol and lexing the rest as ordinary tokens. A quote
        // symbol surviving on its own therefore means the text is not well-formed, and
        // normalizing it would produce a canonical form for something that is not an expression.
        if (tokens.Any(token => token.Type
                is MariaDBLexer.SINGLE_QUOTE_SYMB
                or MariaDBLexer.DOUBLE_QUOTE_SYMB
                or MariaDBLexer.REVERSE_QUOTE_SYMB))
        {
            return null;
        }

        return tokens;
    }

    // Removes grouping that carries no meaning: the pair MySQL wraps around every CHECK clause,
    // and the pairs it adds around each operand of a boolean operator (`a AND b` is reported as
    // `(a) AND (b)`).
    //
    // Only a group that cannot change how the expression parses is removed. A group is safe to
    // unwrap when every operator directly inside it binds TIGHTER than the operators on either
    // side of it, so removing the parentheses cannot re-associate anything: `(a > 0) AND b`
    // unwraps because `>` binds tighter than `AND`, while `(a OR b) AND c` does not, since
    // dropping those parentheses would silently change the predicate to `a OR (b AND c)`.
    private static List<IToken> StripRedundantParentheses(List<IToken> tokens)
    {
        // Repeated to a fixed point: unwrapping an outer group can expose a newly-redundant
        // inner one, as in MySQL's `((a > 0) and (b < 1))`.
        while (true)
        {
            var stripped = StripOnePass(tokens);

            if (stripped.Count == tokens.Count)
            {
                return tokens;
            }

            tokens = stripped;
        }
    }

    private static List<IToken> StripOnePass(List<IToken> tokens)
    {
        for (var open = 0; open < tokens.Count; open++)
        {
            if (tokens[open].Type != MariaDBLexer.LR_BRACKET)
            {
                continue;
            }

            var close = FindMatch(tokens, open);

            if (close < 0 || !IsRedundantGroup(tokens, open, close))
            {
                continue;
            }

            var result = new List<IToken>(tokens.Count - 2);
            result.AddRange(tokens.GetRange(0, open));
            result.AddRange(tokens.GetRange(open + 1, close - open - 1));
            result.AddRange(tokens.GetRange(close + 1, tokens.Count - close - 1));

            return result;
        }

        return tokens;
    }

    private static int FindMatch(List<IToken> tokens, int open)
    {
        var depth = 0;

        for (var i = open; i < tokens.Count; i++)
        {
            depth += tokens[i].Type switch
            {
                MariaDBLexer.LR_BRACKET => 1,
                MariaDBLexer.RR_BRACKET => -1,
                _ => 0,
            };

            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsRedundantGroup(List<IToken> tokens, int open, int close)
    {
        // A group preceded by a name is an argument list or a function call, not grouping.
        if (open > 0 && tokens[open - 1].Type is MariaDBLexer.ID or MariaDBLexer.STRING_LITERAL)
        {
            return false;
        }

        // A comma inside means it is a list (`IN (1, 2, 3)`), not a grouped subexpression.
        var inner = LoosestPrecedenceWithin(tokens, open + 1, close, out var hasComma);

        if (hasComma)
        {
            return false;
        }

        // The operators surrounding the group. Absent (the group spans the whole expression)
        // counts as loosest, so an outer wrapper always unwraps.
        var before = open > 0 ? Precedence(tokens[open - 1].Type) : int.MaxValue;
        var after = close < tokens.Count - 1 ? Precedence(tokens[close + 1].Type) : int.MaxValue;
        var outer = Math.Min(before, after);

        return inner < outer;
    }

    // The loosest-binding (highest-precedence-number) operator directly inside the group,
    // ignoring anything nested deeper since that is already parenthesized. Returns the tightest
    // possible value when the group holds no operator at all, so `(a)` always unwraps.
    private static int LoosestPrecedenceWithin(
        List<IToken> tokens, int start, int end, out bool hasComma)
    {
        var loosest = int.MinValue;
        var depth = 0;
        hasComma = false;

        for (var i = start; i < end; i++)
        {
            switch (tokens[i].Type)
            {
                case MariaDBLexer.LR_BRACKET:
                    depth++;
                    continue;
                case MariaDBLexer.RR_BRACKET:
                    depth--;
                    continue;
                case MariaDBLexer.COMMA when depth == 0:
                    hasComma = true;
                    continue;
            }

            if (depth == 0)
            {
                loosest = Math.Max(loosest, Precedence(tokens[i].Type));
            }
        }

        return loosest;
    }

    // Binding strength, loosest first — larger binds looser. Only the operators whose relative
    // order decides whether a parenthesis can be dropped need distinguishing; everything else is
    // an operand and binds tightest.
    private static int Precedence(int tokenType) => tokenType switch
    {
        MariaDBLexer.OR or MariaDBLexer.XOR => 5,
        MariaDBLexer.AND => 4,
        MariaDBLexer.NOT => 3,
        MariaDBLexer.BETWEEN or MariaDBLexer.LIKE or MariaDBLexer.IN
            or MariaDBLexer.REGEXP or MariaDBLexer.RLIKE or MariaDBLexer.IS => 2,
        MariaDBLexer.EQUAL_SYMBOL or MariaDBLexer.GREATER_SYMBOL or MariaDBLexer.LESS_SYMBOL
            or MariaDBLexer.EXCLAMATION_SYMBOL => 1,
        _ => int.MinValue,
    };

    private static string Render(List<IToken> tokens)
    {
        var sb = new StringBuilder();

        // Tracked rather than read from tokens[i - 1], because a dropped token (a charset
        // introducer renders as nothing) must not still influence spacing.
        IToken? previous = null;

        for (var i = 0; i < tokens.Count; i++)
        {
            var text = NormalizeToken(tokens[i]);

            if (text.Length == 0)
            {
                continue;
            }

            if (sb.Length > 0 && NeedsSpaceBefore(tokens[i], previous))
            {
                sb.Append(' ');
            }

            sb.Append(text);
            previous = tokens[i];
        }

        return sb.ToString();
    }

    private static string NormalizeToken(IToken token)
    {
        // A backtick-quoted identifier lexes as STRING_LITERAL, so the two are told apart by the
        // quote character rather than by token type. `price` (as the engines report it) and price
        // (as source writes it) are the same column, so the backticks come off. Case is NOT
        // folded either way: the engines report an identifier with the case it was declared in,
        // and folding would merge distinct columns.
        if (token.Type == MariaDBLexer.REVERSE_QUOTE_ID
            || (token.Type == MariaDBLexer.STRING_LITERAL && token.Text.StartsWith('`')))
        {
            return token.Text.Trim('`');
        }

        return token.Type switch
        {
            // A charset introducer names how the literal is read, not what the predicate tests.
            MariaDBLexer.STRING_CHARSET_NAME => string.Empty,

            MariaDBLexer.STRING_LITERAL => NormalizeStringLiteral(token.Text),

            // Case is folded only for tokens that can ONLY be grammar. The MariaDB grammar has
            // close to a thousand keyword tokens, most of which are also legal unquoted column
            // names — `name` lexes as a keyword, not as ID — so folding by "is it a keyword
            // token" would destroy the case of an ordinary column. Anything not known to be
            // grammar is left exactly as written.
            _ => IsCaseInsensitiveGrammar(token.Type) ? token.Text.ToUpperInvariant() : token.Text,
        };
    }

    // The tokens that can never be a column reference, and whose case therefore carries no
    // information: the operators and punctuation, plus the keywords that can only appear as
    // operators in an expression. Both engines report these lower-cased regardless of how they
    // were written, so they are folded to one spelling.
    private static bool IsCaseInsensitiveGrammar(int tokenType) => tokenType
        is MariaDBLexer.AND or MariaDBLexer.OR or MariaDBLexer.NOT or MariaDBLexer.XOR
        or MariaDBLexer.IS or MariaDBLexer.NULL_LITERAL or MariaDBLexer.TRUE or MariaDBLexer.FALSE
        or MariaDBLexer.LIKE or MariaDBLexer.REGEXP or MariaDBLexer.RLIKE
        or MariaDBLexer.BETWEEN or MariaDBLexer.IN or MariaDBLexer.EXISTS
        or MariaDBLexer.CASE or MariaDBLexer.WHEN or MariaDBLexer.THEN or MariaDBLexer.ELSE
        or MariaDBLexer.END or MariaDBLexer.INTERVAL or MariaDBLexer.BINARY
        or MariaDBLexer.DIV or MariaDBLexer.MOD or MariaDBLexer.ESCAPE
        or MariaDBLexer.LR_BRACKET or MariaDBLexer.RR_BRACKET or MariaDBLexer.COMMA
        or MariaDBLexer.DOT or MariaDBLexer.EQUAL_SYMBOL or MariaDBLexer.GREATER_SYMBOL
        or MariaDBLexer.LESS_SYMBOL or MariaDBLexer.EXCLAMATION_SYMBOL
        or MariaDBLexer.BIT_NOT_OP or MariaDBLexer.BIT_OR_OP or MariaDBLexer.BIT_AND_OP
        or MariaDBLexer.BIT_XOR_OP or MariaDBLexer.STAR or MariaDBLexer.DIVIDE
        or MariaDBLexer.MODULE or MariaDBLexer.PLUS or MariaDBLexer.MINUS;

    // Re-quotes a literal with one spelling, so text written with double quotes and the same text
    // reported with single quotes agree. The charset introducer is dropped as its own token and
    // the escaping is undone before lexing, so only the quoting is left to reconcile here.
    private static string NormalizeStringLiteral(string text)
    {
        var value = text;

        if (value.Length >= 2
            && (value[0] == '\'' || value[0] == '"')
            && value[^1] == value[0])
        {
            value = value[1..^1];
        }

        // Re-quoted with a single spelling, so a literal written with double quotes and the same
        // one reported with single quotes agree.
        return $"'{value}'";
    }

    // Spacing is uniform — one space between tokens — except around punctuation, where a space
    // would be noise. The engines re-space freely, so only the token sequence is significant.
    private static bool NeedsSpaceBefore(IToken token, IToken? previous)
    {
        if (token.Type is MariaDBLexer.RR_BRACKET or MariaDBLexer.COMMA or MariaDBLexer.DOT)
        {
            return false;
        }

        return previous is null
            || previous.Type is not (MariaDBLexer.LR_BRACKET or MariaDBLexer.DOT);
    }
}
