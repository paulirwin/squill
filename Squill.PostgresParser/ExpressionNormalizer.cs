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

            // Must precede BinaryExpression for the same reason LikeExpression does — IN is
            // carried as a BinaryExpression, and it desugars rather than rendering as-is.
            case BinaryExpression { Operator: BuiltInOperator { Operator:
                    PostgresBuiltInBinaryOperator.In or PostgresBuiltInBinaryOperator.NotIn } } inList:
                return WriteInList(sb, inList);

            case BinaryExpression binary:
                return WriteBinary(sb, binary.Left, binary.Operator, binary.Right);

            case QuantifiedComparisonExpression quantified:
                return WriteQuantifiedComparison(sb, quantified);

            case ArrayExpression array:
                return WriteArray(sb, array);

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

    // A cast is erased when it wraps a plain operand — a literal or a column reference.
    //
    // PostgreSQL types every operand against what it is combined with and reports the result, so
    // such a cast says nothing the source chose. Measured on a live server: `name <> ''` and
    // `name <> ''::text` are BOTH stored as `name <> ''::text`; `price > 0` as
    // `price > (0)::numeric`; and, where a column needs widening, `price * quantity` as
    // `price * (quantity)::numeric`.
    //
    // Crucially the engine does not distinguish an inferred cast from a written one: a declared
    // `quantity::numeric > 0` and an inferred widening both come back as `(quantity)::numeric`.
    // Since the two are indistinguishable once stored, treating them as equal loses nothing that
    // could survive a round trip — whereas keeping them apart would make an unchanged expression
    // re-diff forever.
    //
    // A cast wrapping anything more complex (a call, an operator expression) is left in place:
    // it has not been measured to be inferable, so it is treated as meaningful.
    private static bool WriteTypecast(StringBuilder sb, TypecastExpression typecast)
    {
        switch (Unwrap(typecast.Expression))
        {
            // A negative numeric constant is stored as a QUOTED literal carrying the sign
            // (`x > -1` comes back as `x > '-1'::integer`), so the quotes are stripped when what
            // they wrap is a number. Otherwise a declared -1 and the extracted '-1' would never
            // agree. A genuine string literal keeps its quotes, so '1' stays distinct from 1.
            case LiteralExpression literal:
                sb.Append(UnquoteNumeric(literal.Text));
                return true;

            case ColumnReferenceExpression column:
                sb.Append(column.Identifier.Name);
                return true;
        }

        if (!Write(sb, typecast.Expression))
        {
            return false;
        }

        sb.Append("::").Append(typecast.DataType.TypeName);
        return true;
    }

    // Drops the quotes around a literal that is really a number. PostgreSQL renders a signed
    // numeric constant as a quoted string with a cast (`'-1'::integer`), so without this a
    // declared `-1` and the same constant read back would never agree. Anything that is not a
    // number keeps its quotes, so the string '1' stays distinct from the number 1.
    private static string UnquoteNumeric(string text)
    {
        if (text.Length < 2 || text[0] != '\'' || text[^1] != '\'')
        {
            return text;
        }

        var inner = text[1..^1];

        return decimal.TryParse(inner, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _)
            ? inner
            : text;
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
        // A LIKE with an ESCAPE is stored as a call to the internal like_escape() function
        // (`code ~~ like_escape('%!%%'::text, '!'::text)`), not as the LIKE … ESCAPE spelling.
        // Rendering that call here would be encoding an implementation detail from a single
        // measurement, so the expression is refused and falls back to not participating in
        // identity — see issue #171.
        if (like.Escape is not null)
        {
            return false;
        }

        return WriteBinary(sb, like.Left, like.Operator, like.Right);
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

    /// <summary>
    /// Desugars an <c>IN</c> list into the quantified comparison PostgreSQL stores it as, so
    /// the declared and extracted spellings of one predicate reduce to one token (issue #170).
    ///
    /// <para>
    /// Measured on <c>postgres:latest</c>, and none of it is guessable from the grammar:
    /// <c>q IN (1, 2)</c> is stored as <c>q = ANY (ARRAY[1, 2])</c>, while <c>q NOT IN (1, 2)</c>
    /// becomes <c>q &lt;&gt; ALL (ARRAY[1, 2])</c> — a negated <c>ANY</c> is <em>not</em> what
    /// the engine produces. And a <em>single-element</em> list collapses to a plain comparison
    /// with no array at all: <c>q IN (1)</c> is stored as <c>q = 1</c>.
    /// </para>
    /// </summary>
    private static bool WriteInList(StringBuilder sb, BinaryExpression inList)
    {
        var negated = inList.Operator is BuiltInOperator
        {
            Operator: PostgresBuiltInBinaryOperator.NotIn,
        };

        var op = new BuiltInOperator(negated
            ? PostgresBuiltInBinaryOperator.NotEqual
            : PostgresBuiltInBinaryOperator.Equal);

        // The right operand is an ArrayExpression for a value list, and something else only for
        // the subquery form — which the visitor refuses before reaching here.
        if (inList.Right is not ArrayExpression array)
        {
            return false;
        }

        // One element is not stored as an array, so it must not be normalized as one.
        if (array.Elements.Count == 1)
        {
            return WriteBinary(sb, inList.Left, op, array.Elements[0]);
        }

        return WriteQuantified(
            sb, inList.Left, op,
            negated ? ComparisonQuantifier.All : ComparisonQuantifier.Any,
            array);
    }

    private static bool WriteQuantifiedComparison(
        StringBuilder sb, QuantifiedComparisonExpression quantified)
        => WriteQuantified(
            sb, quantified.Left, quantified.Operator, quantified.Quantifier, quantified.Right);

    // `(left OP ANY (right))` — the shape pg_get_constraintdef reports, with the quantifier's
    // operand parenthesized separately from the comparison.
    private static bool WriteQuantified(
        StringBuilder sb, Expression left, Operator op, ComparisonQuantifier quantifier,
        Expression right)
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

        sb.Append(' ').Append(opText).Append(' ')
            .Append(quantifier == ComparisonQuantifier.All ? "ALL" : "ANY")
            .Append(" (");

        if (!Write(sb, right))
        {
            return false;
        }

        sb.Append("))");

        return true;
    }

    // `ARRAY[a, b]` — element order is preserved rather than sorted, because PostgreSQL stores
    // the order it was given (measured: `q IN (2, 1)` comes back as `ARRAY[2, 1]`), so a
    // reordered list is a genuinely different predicate.
    private static bool WriteArray(StringBuilder sb, ArrayExpression array)
    {
        sb.Append("ARRAY[");

        for (var i = 0; i < array.Elements.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            if (!Write(sb, array.Elements[i]))
            {
                return false;
            }
        }

        sb.Append(']');

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
            // SIMILAR TO is stored as a regex rewrite whose exact form is not measured.
            //
            // IN / NOT IN are deliberately absent rather than missing: they never render as
            // themselves, because PostgreSQL stores them desugared (`= ANY (ARRAY[…])` /
            // `<> ALL (ARRAY[…])`). WriteInList rewrites them into that form before any
            // operator text is needed, so reaching here with one means the desugaring was
            // skipped — and returning null refuses rather than emitting an `IN` the engine
            // would never report back (issue #170).
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
            // A sign applied to a numeric constant folds into the constant, because that is how
            // PostgreSQL stores it: `x > -1` comes back as `x > '-1'::integer`, one signed
            // literal rather than a negation of 1. Emitting it as a signed literal is what makes
            // the declared and extracted spellings agree.
            if (prefix is "-" or "+" && Unwrap(unary.Expression) is LiteralExpression literal)
            {
                var text = UnquoteNumeric(literal.Text);

                sb.Append(prefix == "-" && !text.StartsWith('-') ? $"-{text}" : text);
                return true;
            }

            sb.Append('(').Append(prefix);

            // A word operator needs a separator; a sign must abut its operand.
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
