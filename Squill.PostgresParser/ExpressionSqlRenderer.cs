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

            case ParenthesizedExpression parenthesized:
                sb.Append('(');
                Write(sb, parenthesized.Expression, bareIdentifiers);
                sb.Append(')');
                break;

            case UnaryExpression unary:
                WriteUnary(sb, unary, bareIdentifiers);
                break;

            case BinaryExpression binary:
                Write(sb, binary.Left, bareIdentifiers);
                sb.Append(' ').Append(BinaryOperatorText(binary.Operator)).Append(' ');
                Write(sb, binary.Right, bareIdentifiers);
                break;

            case FunctionApplicationExpression function:
                WriteFunction(sb, function, bareIdentifiers);
                break;

            case TypecastExpression typecast:
                Write(sb, typecast.Expression, bareIdentifiers);
                sb.Append("::").Append(typecast.DataType.TypeName);
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
            _ => throw new NotImplementedException(
                $"Rendering binary operator {builtIn.Operator} to SQL is not yet implemented"),
        };
    }
}
