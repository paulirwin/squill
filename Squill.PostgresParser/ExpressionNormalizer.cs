using System.Text;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

/// <summary>
/// Reduces a parsed <see cref="Expression"/> to a canonical token, so a predicate as DECLARED in
/// source and the same predicate as EXTRACTED from a live database compare equal (issue #156).
///
/// This is what lets a CHECK predicate and a generated column's expression take part in their
/// element's identity. Without it the property has to be excluded from the Merkle hash, and
/// redefining a predicate under the same name changes no hash, produces no delta, and is
/// silently ignored while the old predicate stays enforced.
///
/// PostgreSQL does not merely re-spell the expression it stores, it rewrites its structure, so
/// comparing text (or comparing parse trees directly) is not enough. Measured against a live
/// server, the rewrites this reverses are:
///
/// <list type="bullet">
/// <item>a cast injected onto a literal — <c>price &gt; 0</c> is stored as
///   <c>price &gt; (0)::numeric</c>, so the cast carries no information the source expressed;</item>
/// <item><c>LIKE</c> spelled as its underlying operator <c>~~</c> (and <c>NOT LIKE</c> as
///   <c>!~~</c>);</item>
/// <item><c>BETWEEN</c> desugared into a pair of comparisons joined by <c>AND</c> (and
///   <c>NOT BETWEEN</c> into a pair joined by <c>OR</c>);</item>
/// <item>grouping parentheses the engine adds around every subexpression.</item>
/// </list>
///
/// A cast written in the source is NOT noise and is preserved: <c>price::integer &gt; 0</c> and
/// <c>price &gt; 0</c> are different predicates. The two are told apart structurally — the engine
/// parenthesizes the operand it casts (<c>(0)::numeric</c>), a source cast does not.
///
/// The rewrite is idempotent, so normalizing an already-canonical expression is a no-op and a
/// predicate cannot oscillate between two spellings.
///
/// Anything not recognized here makes <see cref="TryNormalize"/> return <c>false</c> rather than
/// emit a guess. A wrong canonical form is worse than none: it makes an unchanged predicate look
/// changed and redeploys the object on every deploy, whereas no canonical form merely leaves the
/// property out of the hash — the known gap this class narrows.
/// </summary>
public static class ExpressionNormalizer
{
    /// <summary>
    /// Produces the canonical form of an expression given as TEXT — either as the user declared
    /// it or as the catalog reported it — by parsing it first. Returns <c>false</c> when the text
    /// does not parse or contains a construct with no known canonical form.
    /// </summary>
    /// <remarks>
    /// The text is parsed as an index predicate, which is the grammar's expression position, so a
    /// bare expression can be parsed without a statement around it.
    /// </remarks>
    public static bool TryNormalize(string expressionText, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(expressionText))
        {
            return false;
        }

        Expression expression;

        try
        {
            var root = new AntlrPostgresParser()
                .Parse($"CREATE INDEX squill_normalize ON squill_normalize (c) WHERE {expressionText};");

            if (root.Statements.Count != 1
                || root.Statements[0] is not CreateIndexStatement { WhereClause: { } predicate })
            {
                return false;
            }

            expression = predicate;
        }
        catch (PostgresParseException)
        {
            return false;
        }
        catch (NotImplementedException)
        {
            // A construct the visitor does not map yet (e.g. `= ANY (…)`, issue #170). Treated
            // like any other unnormalizable expression rather than failing the build.
            return false;
        }

        return TryNormalize(expression, out canonical);
    }

    /// <summary>
    /// Produces the canonical form of <paramref name="expression"/>, or returns <c>false</c>
    /// when it contains a construct with no known canonical form.
    /// </summary>
    public static bool TryNormalize(Expression expression, out string canonical)
    {
        var sb = new StringBuilder();

        if (!Write(sb, expression))
        {
            canonical = string.Empty;
            return false;
        }

        canonical = sb.ToString();
        return true;
    }

    private static bool Write(StringBuilder sb, Expression expression)
    {
        switch (expression)
        {
            // Grouping parentheses carry no meaning of their own: the tree already records the
            // structure, and the engine adds its own around everything. Canonical form re-adds
            // them uniformly around each compound node below, so they are dropped here.
            case ParenthesizedExpression parenthesized:
                return Write(sb, parenthesized.Expression);

            case ColumnReferenceExpression column:
                // Bare, so the source's `"price"` and the catalog's `price` agree.
                sb.Append(column.Identifier.Name);
                return true;

            case LiteralExpression literal:
                sb.Append(literal.Text);
                return true;

            case TypecastExpression typecast:
                return WriteTypecast(sb, typecast);

            case BetweenExpression between:
                return WriteBetween(sb, between);

            // Must precede BinaryExpression: LikeExpression derives from it, and an ESCAPE
            // operand changes what the predicate matches, so it cannot be dropped.
            case LikeExpression like:
                return WriteLike(sb, like);

            case BinaryExpression binary:
                return WriteBinary(sb, binary.Left, binary.Operator, binary.Right);

            case UnaryExpression unary:
                return WriteUnary(sb, unary);

            case FunctionApplicationExpression function:
                return WriteFunction(sb, function);

            // A keyword constant (CURRENT_TIMESTAMP and friends) is stored by PostgreSQL with
            // the spelling it was given, so it is already canonical.
            case KeywordExpression keyword:
                sb.Append(keyword.Keyword);
                if (keyword.Precision is { } precision)
                {
                    sb.Append('(').Append(precision).Append(')');
                }
                return true;

            // Everything else — a custom unary operator, AT TIME ZONE, COLLATE, the
            // func_expr_common_subexpr forms, an array or subquery construct — has no canonical
            // form established by measurement, so refuse rather than guess.
            default:
                return false;
        }
    }

    // A cast onto a LITERAL is erased; a cast onto anything else is kept.
    //
    // PostgreSQL types every literal in a predicate against the column it is compared with and
    // reports the result, so the cast says nothing the source chose: measured on a live server,
    // `name <> ''` and `name <> ''::text` are BOTH stored as `name <> ''::text`, and `price > 0`
    // as `price > (0)::numeric` (numeric literals additionally get parenthesized). Erasing it
    // makes the declared and extracted spellings converge, and — because the two source
    // spellings are indistinguishable once stored — loses nothing.
    //
    // A cast onto anything else IS the source's own: `price::integer > 0` differs from
    // `price > 0` and must stay distinct, so it is preserved.
    private static bool WriteTypecast(StringBuilder sb, TypecastExpression typecast)
    {
        if (Unwrap(typecast.Expression) is LiteralExpression literal)
        {
            sb.Append(literal.Text);
            return true;
        }

        if (!Write(sb, typecast.Expression))
        {
            return false;
        }

        sb.Append("::").Append(typecast.DataType.TypeName);
        return true;
    }

    // Strips the grouping parentheses PostgreSQL adds around a cast operand, so the operand can
    // be classified by what it actually is.
    private static Expression Unwrap(Expression expression)
        => expression is ParenthesizedExpression parenthesized
            ? Unwrap(parenthesized.Expression)
            : expression;

    // `a BETWEEN x AND y` is stored as `(a >= x) AND (a <= y)`, and the NOT form as
    // `(a < x) OR (a > y)`. Emitting the desugared shape makes the declared and extracted
    // spellings converge. SYMMETRIC has no measured canonical form, so it is refused.
    private static bool WriteBetween(StringBuilder sb, BetweenExpression between)
    {
        if (between.IsSymmetric)
        {
            return false;
        }

        var (lowerOp, upperOp, joiner) = between.IsNegated
            ? ("<", ">", "OR")
            : (">=", "<=", "AND");

        sb.Append("((");
        if (!Write(sb, between.Operand))
        {
            return false;
        }

        sb.Append(' ').Append(lowerOp).Append(' ');
        if (!Write(sb, between.Lower))
        {
            return false;
        }

        sb.Append(") ").Append(joiner).Append(" (");
        if (!Write(sb, between.Operand))
        {
            return false;
        }

        sb.Append(' ').Append(upperOp).Append(' ');
        if (!Write(sb, between.Upper))
        {
            return false;
        }

        sb.Append("))");
        return true;
    }

    private static bool WriteLike(StringBuilder sb, LikeExpression like)
    {
        if (!WriteBinary(sb, like.Left, like.Operator, like.Right, closeParen: like.Escape is null))
        {
            return false;
        }

        if (like.Escape is not { } escape)
        {
            return true;
        }

        sb.Append(" ESCAPE ");
        if (!Write(sb, escape))
        {
            return false;
        }

        sb.Append(')');
        return true;
    }

    private static bool WriteBinary(
        StringBuilder sb, Expression left, Operator op, Expression right, bool closeParen = true)
    {
        if (OperatorText(op) is not { } opText)
        {
            return false;
        }

        sb.Append('(');
        if (!Write(sb, left))
        {
            return false;
        }

        sb.Append(' ').Append(opText).Append(' ');
        if (!Write(sb, right))
        {
            return false;
        }

        if (closeParen)
        {
            sb.Append(')');
        }

        return true;
    }

    private static bool WriteFunction(StringBuilder sb, FunctionApplicationExpression function)
    {
        // Lower-cased because PostgreSQL reports an unquoted function name folded to lower case,
        // so a source `UPPER(name)` and an extracted `upper(name)` are the same call.
        sb.Append(function.Name.ToLowerInvariant()).Append('(');

        for (var i = 0; i < function.Arguments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            var argument = function.Arguments[i];

            // A named argument (`f(x => 1)`) has no measured canonical form.
            if (argument.ParamName is not null)
            {
                return false;
            }

            if (!Write(sb, argument.Expression))
            {
                return false;
            }
        }

        sb.Append(')');
        return true;
    }

    // The canonical spelling of each operator. LIKE and its negation collapse onto the
    // operators PostgreSQL actually stores (`~~` / `!~~`), which is how a declared `LIKE` and an
    // extracted `~~` reduce to one token. A custom operator is passed through verbatim: `~~`
    // arrives as one, and so does any user- or extension-defined operator, which we reproduce
    // rather than interpret.
    private static string? OperatorText(Operator op) => op switch
    {
        CustomOperator custom => custom.Symbol,
        BuiltInOperator { Operator: var builtIn } => builtIn switch
        {
            PostgresBuiltInBinaryOperator.Like => "~~",
            PostgresBuiltInBinaryOperator.NotLike => "!~~",
            PostgresBuiltInBinaryOperator.ILike => "~~*",
            PostgresBuiltInBinaryOperator.NotILike => "!~~*",
            PostgresBuiltInBinaryOperator.Exponentiation => "^",
            PostgresBuiltInBinaryOperator.Multiplication => "*",
            PostgresBuiltInBinaryOperator.Division => "/",
            PostgresBuiltInBinaryOperator.Modulo => "%",
            PostgresBuiltInBinaryOperator.Addition => "+",
            PostgresBuiltInBinaryOperator.Subtraction => "-",
            PostgresBuiltInBinaryOperator.LessThan => "<",
            PostgresBuiltInBinaryOperator.LessThanEqual => "<=",
            PostgresBuiltInBinaryOperator.GreaterThan => ">",
            PostgresBuiltInBinaryOperator.GreaterThanEqual => ">=",
            PostgresBuiltInBinaryOperator.Equal => "=",
            PostgresBuiltInBinaryOperator.NotEqual => "<>",
            PostgresBuiltInBinaryOperator.And => "AND",
            PostgresBuiltInBinaryOperator.Or => "OR",
            PostgresBuiltInBinaryOperator.LeftShift => "<<",
            PostgresBuiltInBinaryOperator.RightShift => ">>",
            // SIMILAR TO is stored as a regex rewrite whose exact form is not measured, and
            // IN as `= ANY (ARRAY[…])`, which does not parse today (issue #170).
            _ => null,
        },
        _ => null,
    };

    // PostgreSQL's unary operators are a mix of prefix (NOT, sign) and postfix (the IS … family).
    // The postfix ones have two source spellings apiece — `NOTNULL` and `IS NOT NULL` — which the
    // engine reports as one, so both collapse onto the same canonical text here.
    private static bool WriteUnary(StringBuilder sb, UnaryExpression unary)
    {
        if (PrefixOperatorText(unary.Operator) is { } prefix)
        {
            sb.Append('(').Append(prefix);

            // A word operator needs a separator; a sign must abut its operand so `- 1` and `-1`
            // agree.
            if (prefix == "NOT")
            {
                sb.Append(' ');
            }

            if (!Write(sb, unary.Expression))
            {
                return false;
            }

            sb.Append(')');
            return true;
        }

        if (PostfixOperatorText(unary.Operator) is not { } postfix)
        {
            return false;
        }

        sb.Append('(');
        if (!Write(sb, unary.Expression))
        {
            return false;
        }

        sb.Append(' ').Append(postfix).Append(')');
        return true;
    }

    private static string? PrefixOperatorText(PostgresBuiltInUnaryOperator op) => op switch
    {
        PostgresBuiltInUnaryOperator.Not => "NOT",
        PostgresBuiltInUnaryOperator.Negate => "-",
        PostgresBuiltInUnaryOperator.Plus => "+",
        _ => null,
    };

    private static string? PostfixOperatorText(PostgresBuiltInUnaryOperator op) => op switch
    {
        PostgresBuiltInUnaryOperator.IsNull or PostgresBuiltInUnaryOperator.IsNullKeyword
            => "IS NULL",
        PostgresBuiltInUnaryOperator.NotNull or PostgresBuiltInUnaryOperator.IsNotNull
            => "IS NOT NULL",
        PostgresBuiltInUnaryOperator.IsTrue => "IS TRUE",
        PostgresBuiltInUnaryOperator.IsNotTrue => "IS NOT TRUE",
        PostgresBuiltInUnaryOperator.IsFalse => "IS FALSE",
        PostgresBuiltInUnaryOperator.IsNotFalse => "IS NOT FALSE",
        PostgresBuiltInUnaryOperator.IsUnknown => "IS UNKNOWN",
        PostgresBuiltInUnaryOperator.IsNotUnknown => "IS NOT UNKNOWN",
        _ => null,
    };
}
