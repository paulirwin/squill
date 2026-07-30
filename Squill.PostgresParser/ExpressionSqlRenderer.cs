using System.Text;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

/// <summary>
/// Renders a parsed <see cref="Expression"/> back to executable PostgreSQL text.
/// Used to carry an index's WHERE predicate from the parsed syntax tree into the
/// model as a SQL string, and from there back out to a CREATE INDEX script.
///
/// The output is normalized (identifiers double-quoted, single spaces around
/// operators) rather than byte-identical to Postgres's own <c>pg_get_expr</c>
/// canonicalization; it only needs to be valid, equivalent SQL.
/// </summary>
public static class ExpressionSqlRenderer
{
    /// <summary>
    /// Identifiers that must be rendered bare rather than double-quoted. Used for a domain
    /// CHECK, where <c>VALUE</c> is a keyword standing for the value being checked — quoting
    /// it (<c>"VALUE"</c>) turns it into a (nonexistent) column reference and Postgres
    /// rejects it with "column \"VALUE\" does not exist".
    /// </summary>
    private static readonly HashSet<string> NoBareIdentifiers = new(StringComparer.Ordinal);

    public static string Render(Expression expression)
        => Render(expression, NoBareIdentifiers);

    /// <summary>
    /// Renders an expression, leaving any identifier in <paramref name="bareIdentifiers"/>
    /// unquoted (e.g. the domain <c>VALUE</c> keyword).
    /// </summary>
    public static string Render(Expression expression, IReadOnlySet<string> bareIdentifiers)
    {
        var sb = new StringBuilder();
        Write(sb, expression, bareIdentifiers);
        return sb.ToString();
    }

    private static void Write(StringBuilder sb, Expression expression)
        => Write(sb, expression, NoBareIdentifiers);

    private static void Write(StringBuilder sb, Expression expression, IReadOnlySet<string> bareIdentifiers)
    {
        switch (expression)
        {
            case ColumnReferenceExpression columnReference:
                if (bareIdentifiers.Contains(columnReference.Identifier.Name))
                {
                    sb.Append(columnReference.Identifier.Name);
                }
                else
                {
                    sb.Append('"').Append(columnReference.Identifier.Name).Append('"');
                }
                break;

            case LiteralExpression literal:
                // Text preserves the original source form (quoted strings, numeric
                // constants, TRUE/FALSE), which is already valid SQL.
                sb.Append(literal.Text);
                break;

            // `interval '1 day'` — the type prefix is part of the constant's meaning, so it
            // renders back out with it.
            case TypedLiteralExpression typedLiteral:
                sb.Append(typedLiteral.TypeName).Append(' ').Append(typedLiteral.Literal.Text);
                if (typedLiteral.Modifier is { } modifier)
                {
                    sb.Append(' ').Append(modifier);
                }
                break;

            case ParenthesizedExpression parenthesized:
                sb.Append('(');
                Write(sb, parenthesized.Expression, bareIdentifiers);
                sb.Append(')');
                break;

            // The parentheses are required by the syntax rather than optional grouping, so
            // they are always written back out.
            case IndirectionExpression indirection:
                sb.Append('(');
                Write(sb, indirection.Expression, bareIdentifiers);
                sb.Append(')');
                foreach (var element in indirection.Elements)
                {
                    sb.Append(element);
                }
                break;

            case UnaryExpression unary:
                WriteUnary(sb, unary, bareIdentifiers);
                break;

            // Must precede BinaryExpression — LikeExpression derives from it, and the
            // trailing ESCAPE would otherwise be dropped.
            case LikeExpression like:
                Write(sb, like.Left, bareIdentifiers);
                sb.Append(' ').Append(BinaryOperatorText(like.Operator)).Append(' ');
                Write(sb, like.Right, bareIdentifiers);
                if (like.Escape is { } escape)
                {
                    sb.Append(" ESCAPE ");
                    Write(sb, escape, bareIdentifiers);
                }
                break;

            // Must precede BinaryExpression: IN carries its operands as an ArrayExpression, but
            // renders them as the parenthesized list it was written as — `x IN (1, 2)`, not
            // `x IN ARRAY[1, 2]`, which is not valid SQL (issue #170).
            case BinaryExpression
            {
                Operator: BuiltInOperator { Operator:
                    PostgresBuiltInBinaryOperator.In or PostgresBuiltInBinaryOperator.NotIn },
                Right: ArrayExpression inList,
            } inExpression:
                Write(sb, inExpression.Left, bareIdentifiers);
                sb.Append(' ').Append(BinaryOperatorText(inExpression.Operator)).Append(" (");
                WriteElements(sb, inList.Elements, bareIdentifiers);
                sb.Append(')');
                break;

            case BinaryExpression binary:
                Write(sb, binary.Left, bareIdentifiers);
                sb.Append(' ').Append(BinaryOperatorText(binary.Operator)).Append(' ');
                Write(sb, binary.Right, bareIdentifiers);
                break;

            case BetweenExpression between:
                Write(sb, between.Operand, bareIdentifiers);
                sb.Append(between.IsNegated ? " NOT BETWEEN" : " BETWEEN");
                if (between.IsSymmetric)
                {
                    sb.Append(" SYMMETRIC");
                }
                sb.Append(' ');
                Write(sb, between.Lower, bareIdentifiers);
                sb.Append(" AND ");
                Write(sb, between.Upper, bareIdentifiers);
                break;

            case CollateExpression collate:
                Write(sb, collate.Expression, bareIdentifiers);
                sb.Append(" COLLATE ");
                WriteQualifiedName(sb, collate.Collation);
                break;

            case AtTimeZoneExpression atTimeZone:
                Write(sb, atTimeZone.Expression, bareIdentifiers);
                sb.Append(" AT TIME ZONE ");
                Write(sb, atTimeZone.TimeZone, bareIdentifiers);
                break;

            case CustomUnaryExpression customUnary:
                sb.Append(customUnary.Operator.Symbol).Append(' ');
                Write(sb, customUnary.Expression, bareIdentifiers);
                break;

            case FunctionApplicationExpression function:
                WriteFunction(sb, function, bareIdentifiers);
                break;

            case TypecastExpression typecast:
                Write(sb, typecast.Expression, bareIdentifiers);
                sb.Append("::").Append(typecast.DataType.TypeName);
                break;

            // The func_expr_common_subexpr forms (issue #140). Each renders with the keyword
            // spelling it was written with rather than being rewritten into a call, since
            // Postgres itself preserves these spellings.
            case KeywordExpression keyword:
                sb.Append(keyword.Keyword);
                if (keyword.Precision is { } precision)
                {
                    sb.Append('(').Append(precision).Append(')');
                }
                break;

            case CastExpression cast:
                sb.Append(cast.IsTreat ? "TREAT(" : "CAST(");
                Write(sb, cast.Expression, bareIdentifiers);
                sb.Append(" AS ").Append(cast.DataType.TypeName).Append(')');
                break;

            case ExtractExpression extract:
                sb.Append("EXTRACT(").Append(extract.Field).Append(" FROM ");
                Write(sb, extract.Source, bareIdentifiers);
                sb.Append(')');
                break;

            case SubstringExpression substring:
                WriteSubstring(sb, substring, bareIdentifiers);
                break;

            case TrimExpression trim:
                WriteTrim(sb, trim, bareIdentifiers);
                break;

            case PositionExpression position:
                sb.Append("POSITION(");
                Write(sb, position.Substring, bareIdentifiers);
                sb.Append(" IN ");
                Write(sb, position.Source, bareIdentifiers);
                sb.Append(')');
                break;

            case OverlayExpression overlay:
                WriteOverlay(sb, overlay, bareIdentifiers);
                break;

            case NormalizeExpression normalize:
                sb.Append("NORMALIZE(");
                Write(sb, normalize.Expression, bareIdentifiers);
                if (normalize.Form is { } form)
                {
                    sb.Append(", ").Append(form);
                }
                sb.Append(')');
                break;

            case CollationForExpression collation:
                sb.Append("COLLATION FOR (");
                Write(sb, collation.Expression, bareIdentifiers);
                sb.Append(')');
                break;

            // `x = ANY (ARRAY[…])` (issue #170). The quantifier's operand is parenthesized
            // separately from the comparison, matching how PostgreSQL reports it.
            case QuantifiedComparisonExpression quantified:
                Write(sb, quantified.Left, bareIdentifiers);
                sb.Append(' ').Append(BinaryOperatorText(quantified.Operator)).Append(' ')
                    .Append(quantified.Quantifier == ComparisonQuantifier.All ? "ALL" : "ANY")
                    .Append(" (");
                Write(sb, quantified.Right, bareIdentifiers);
                sb.Append(')');
                break;

            case ArrayExpression array:
                sb.Append("ARRAY[");
                WriteElements(sb, array.Elements, bareIdentifiers);
                sb.Append(']');
                break;

            default:
                throw new NotImplementedException(
                    $"Rendering expression type {expression.GetType().Name} to SQL is not yet implemented");
        }
    }

    private static void WriteUnary(StringBuilder sb, UnaryExpression unary,
        IReadOnlySet<string> bareIdentifiers)
    {
        switch (unary.Operator)
        {
            case PostgresBuiltInUnaryOperator.Not:
                sb.Append("NOT ");
                Write(sb, unary.Expression, bareIdentifiers);
                break;

            case PostgresBuiltInUnaryOperator.Negate:
                sb.Append('-');
                Write(sb, unary.Expression, bareIdentifiers);
                break;

            case PostgresBuiltInUnaryOperator.Plus:
                sb.Append('+');
                Write(sb, unary.Expression, bareIdentifiers);
                break;

            case PostgresBuiltInUnaryOperator.IsNull:
            case PostgresBuiltInUnaryOperator.IsNullKeyword:
                Write(sb, unary.Expression, bareIdentifiers);
                sb.Append(" IS NULL");
                break;

            case PostgresBuiltInUnaryOperator.NotNull:
            case PostgresBuiltInUnaryOperator.IsNotNull:
                Write(sb, unary.Expression, bareIdentifiers);
                sb.Append(" IS NOT NULL");
                break;

            case PostgresBuiltInUnaryOperator.IsTrue:
                Write(sb, unary.Expression, bareIdentifiers);
                sb.Append(" IS TRUE");
                break;

            case PostgresBuiltInUnaryOperator.IsNotTrue:
                Write(sb, unary.Expression, bareIdentifiers);
                sb.Append(" IS NOT TRUE");
                break;

            case PostgresBuiltInUnaryOperator.IsFalse:
                Write(sb, unary.Expression, bareIdentifiers);
                sb.Append(" IS FALSE");
                break;

            case PostgresBuiltInUnaryOperator.IsNotFalse:
                Write(sb, unary.Expression, bareIdentifiers);
                sb.Append(" IS NOT FALSE");
                break;

            case PostgresBuiltInUnaryOperator.IsUnknown:
                Write(sb, unary.Expression, bareIdentifiers);
                sb.Append(" IS UNKNOWN");
                break;

            case PostgresBuiltInUnaryOperator.IsNotUnknown:
                Write(sb, unary.Expression, bareIdentifiers);
                sb.Append(" IS NOT UNKNOWN");
                break;

            default:
                throw new NotImplementedException(
                    $"Rendering unary operator {unary.Operator} to SQL is not yet implemented");
        }
    }

    private static void WriteSubstring(StringBuilder sb, SubstringExpression substring,
        IReadOnlySet<string> bareIdentifiers)
    {
        sb.Append("SUBSTRING(");
        Write(sb, substring.Source, bareIdentifiers);

        if (substring.Similar is { } similar)
        {
            sb.Append(" SIMILAR ");
            Write(sb, similar, bareIdentifiers);
            sb.Append(" ESCAPE ");
            Write(sb, substring.Escape!, bareIdentifiers);
            sb.Append(')');
            return;
        }

        // FROM before FOR — the grammar admits either order, but this one is canonical and
        // means the same thing.
        if (substring.From is { } from)
        {
            sb.Append(" FROM ");
            Write(sb, from, bareIdentifiers);
        }

        if (substring.For is { } forLength)
        {
            sb.Append(" FOR ");
            Write(sb, forLength, bareIdentifiers);
        }

        sb.Append(')');
    }

    private static void WriteTrim(StringBuilder sb, TrimExpression trim,
        IReadOnlySet<string> bareIdentifiers)
    {
        sb.Append("TRIM(").Append(trim.Side.ToString().ToUpperInvariant()).Append(' ');

        if (trim.Characters is { } characters)
        {
            Write(sb, characters, bareIdentifiers);
            sb.Append(" FROM ");
        }

        for (var i = 0; i < trim.Sources.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Write(sb, trim.Sources[i], bareIdentifiers);
        }

        sb.Append(')');
    }

    private static void WriteOverlay(StringBuilder sb, OverlayExpression overlay,
        IReadOnlySet<string> bareIdentifiers)
    {
        sb.Append("OVERLAY(");
        Write(sb, overlay.Source, bareIdentifiers);
        sb.Append(" PLACING ");
        Write(sb, overlay.Replacement, bareIdentifiers);
        sb.Append(" FROM ");
        Write(sb, overlay.From, bareIdentifiers);

        if (overlay.For is { } forLength)
        {
            sb.Append(" FOR ");
            Write(sb, forLength, bareIdentifiers);
        }

        sb.Append(')');
    }

    private static void WriteFunction(StringBuilder sb, FunctionApplicationExpression function,
        IReadOnlySet<string> bareIdentifiers)
    {
        sb.Append(function.Name).Append('(');

        for (var i = 0; i < function.Arguments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Write(sb, function.Arguments[i].Expression, bareIdentifiers);
        }

        sb.Append(')');
    }

    // A collation name is an identifier, and a collation such as "C" is case-sensitive, so
    // each segment is quoted the same way a column reference is.
    private static void WriteQualifiedName(StringBuilder sb, QualifiedName name)
    {
        for (var i = 0; i < name.Segments.Count; i++)
        {
            if (i > 0)
            {
                sb.Append('.');
            }

            sb.Append('"').Append(name.Segments[i].Name).Append('"');
        }
    }

    // A comma-separated operand list, shared by an array constructor and an IN list — the two
    // spellings differ only in their brackets.
    private static void WriteElements(
        StringBuilder sb, IReadOnlyList<Expression> elements, IReadOnlySet<string> bareIdentifiers)
    {
        for (var i = 0; i < elements.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(", ");
            }

            Write(sb, elements[i], bareIdentifiers);
        }
    }

    private static string BinaryOperatorText(Operator op)
    {
        // A general operator (`||`, `->>`, a user-defined one) is carried verbatim, so it
        // renders back out exactly as written.
        if (op is CustomOperator custom)
        {
            return custom.Symbol;
        }

        if (op is not BuiltInOperator builtIn)
        {
            throw new NotImplementedException(
                $"Rendering operator type {op.GetType().Name} to SQL is not yet implemented");
        }

        return builtIn.Operator switch
        {
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
            PostgresBuiltInBinaryOperator.In => "IN",
            PostgresBuiltInBinaryOperator.NotIn => "NOT IN",
            PostgresBuiltInBinaryOperator.LeftShift => "<<",
            PostgresBuiltInBinaryOperator.RightShift => ">>",
            PostgresBuiltInBinaryOperator.Like => "LIKE",
            PostgresBuiltInBinaryOperator.NotLike => "NOT LIKE",
            PostgresBuiltInBinaryOperator.ILike => "ILIKE",
            PostgresBuiltInBinaryOperator.NotILike => "NOT ILIKE",
            PostgresBuiltInBinaryOperator.SimilarTo => "SIMILAR TO",
            PostgresBuiltInBinaryOperator.NotSimilarTo => "NOT SIMILAR TO",
            _ => throw new NotImplementedException(
                $"Rendering binary operator {builtIn.Operator} to SQL is not yet implemented"),
        };
    }
}
