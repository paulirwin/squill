using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitA_expr_compare(PostgreSQLParser.A_expr_compareContext context)
    {
        // a_expr_compare
        //   : a_expr_like ((LT | GT | EQUAL | ...) a_expr_like
        //                 | subquery_Op sub_type (select_with_parens | OPEN_PAREN a_expr CLOSE_PAREN))?
        //
        // The quantified form (`x = ANY (ARRAY[…])`) must be handled before the single-operand
        // check below: it has only ONE a_expr_like, so the `Length: 1` fast path would return
        // the bare left operand and silently drop the comparison (issue #170).
        if (context.subquery_Op() is { } subqueryOp)
        {
            return VisitQuantifiedComparison(context, subqueryOp);
        }

        if (context.a_expr_like() is { Length: 1 })
        {
            return VisitA_expr_like(context.a_expr_like()[0]);
        }

        PostgresBuiltInBinaryOperator op;

        if (context.LT() is not null)
        {
            op = PostgresBuiltInBinaryOperator.LessThan;
        }
        else if (context.GT() is not null)
        {
            op = PostgresBuiltInBinaryOperator.GreaterThan;
        }
        else if (context.EQUAL() is not null)
        {
            op = PostgresBuiltInBinaryOperator.Equal;
        }
        else if (context.LESS_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.LessThanEqual;
        }
        else if (context.GREATER_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.GreaterThanEqual;
        }
        else if (context.NOT_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.NotEqual;
        }
        else
        {
            throw new PostgresParseException("Unexpected binary operator in compare expression");
        }

        if (VisitA_expr_like(context.a_expr_like()[0]) is not Expression left
            || VisitA_expr_like(context.a_expr_like()[1]) is not Expression right)
        {
            throw new PostgresParseException("Unable to parse left or right side of compare expression");
        }

        return new BinaryExpression(
            left,
            new BuiltInOperator(op),
            right
        );
    }

    /// <summary>
    /// A comparison quantified over a set — <c>x = ANY (ARRAY[…])</c>, <c>x &lt;&gt; ALL (…)</c>
    /// — which is the form PostgreSQL stores an <c>IN</c> predicate as (issue #170).
    /// </summary>
    private QuantifiedComparisonExpression VisitQuantifiedComparison(
        PostgreSQLParser.A_expr_compareContext context,
        PostgreSQLParser.Subquery_OpContext subqueryOp)
    {
        if (context.select_with_parens() is not null)
        {
            throw new NotImplementedException(
                "A quantified comparison against a subquery (= ANY (SELECT ...)) is not "
                + "supported: a predicate whose value depends on other rows cannot be compared "
                + "against the target schema. Use an explicit value list.");
        }

        if (context.a_expr() is not { } rightContext)
        {
            // The grammar admits only the subquery and parenthesized-expression forms, and the
            // subquery one is refused above — so this means the grammar changed shape.
            throw new PostgresParseException(
                "Unable to parse the right operand of a quantified comparison");
        }

        if (VisitA_expr_like(context.a_expr_like()[0]) is not Expression left)
        {
            throw new PostgresParseException(
                "Unable to parse the left operand of a quantified comparison");
        }

        return new QuantifiedComparisonExpression(
            left,
            MapSubqueryOperator(subqueryOp),
            MapComparisonQuantifier(context.sub_type()),
            RequireExpression(rightContext));
    }

    // sub_type : ANY | SOME | ALL
    //
    // SOME folds into ANY rather than being kept apart: PostgreSQL treats the two as synonyms
    // and reports SOME back as ANY (measured), so keeping them distinct would make one spelling
    // of an unchanged predicate re-diff on every deploy.
    private static ComparisonQuantifier MapComparisonQuantifier(
        PostgreSQLParser.Sub_typeContext? context)
        => context switch
        {
            null => throw new PostgresParseException(
                "A quantified comparison has no ANY / SOME / ALL quantifier"),
            _ when context.ALL() is not null => ComparisonQuantifier.All,
            _ when context.ANY() is not null || context.SOME() is not null
                => ComparisonQuantifier.Any,
            _ => throw new PostgresParseException(
                "Unexpected quantifier in a quantified comparison"),
        };

    // subquery_Op : all_op | OPERATOR OPEN_PAREN any_operator CLOSE_PAREN | LIKE | NOT LIKE
    //             | ILIKE | NOT ILIKE
    //
    // Note the operator arrives here rather than through the A_expr_compareContext accessors:
    // `=` in `= ANY` is a mathop inside all_op, not the context's own EQUAL token.
    private static Operator MapSubqueryOperator(PostgreSQLParser.Subquery_OpContext context)
    {
        if (context.LIKE() is not null)
        {
            return new BuiltInOperator(context.NOT() is not null
                ? PostgresBuiltInBinaryOperator.NotLike
                : PostgresBuiltInBinaryOperator.Like);
        }

        if (context.ILIKE() is not null)
        {
            return new BuiltInOperator(context.NOT() is not null
                ? PostgresBuiltInBinaryOperator.NotILike
                : PostgresBuiltInBinaryOperator.ILike);
        }

        if (context.all_op() is { } allOp)
        {
            return MapAllOperator(allOp);
        }

        // OPERATOR(schema.op) — carried verbatim, the same as any other qualified operator.
        return new CustomOperator(context.GetText());
    }

    // all_op : Operator | mathop
    //
    // A built-in mathop maps to its enum member so it renders in the one canonical spelling;
    // anything else is a user-defined operator and is carried as written.
    private static Operator MapAllOperator(PostgreSQLParser.All_opContext context)
        => context.mathop()?.GetText() switch
        {
            "=" => new BuiltInOperator(PostgresBuiltInBinaryOperator.Equal),
            "<>" => new BuiltInOperator(PostgresBuiltInBinaryOperator.NotEqual),
            "!=" => new BuiltInOperator(PostgresBuiltInBinaryOperator.NotEqual),
            "<" => new BuiltInOperator(PostgresBuiltInBinaryOperator.LessThan),
            ">" => new BuiltInOperator(PostgresBuiltInBinaryOperator.GreaterThan),
            "<=" => new BuiltInOperator(PostgresBuiltInBinaryOperator.LessThanEqual),
            ">=" => new BuiltInOperator(PostgresBuiltInBinaryOperator.GreaterThanEqual),
            _ => new CustomOperator(context.GetText()),
        };
}