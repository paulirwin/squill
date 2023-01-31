using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitFunc_application(PostgreSQLParser.Func_applicationContext context)
    {
        // TODO: handle names better, i.e. quoted identifiers
        var name = context.func_name().GetText();

        var func = new FunctionApplicationExpression(name);

        if (context.func_arg_expr() is not null
            || context.VARIADIC() is not null
            || context.ALL() is not null
            || context.DISTINCT() is not null
            || context.STAR() is not null)
        {
            throw new NotImplementedException(
                "Support for variadic arguments, VARIADIC, ALL, DISTINCT, and * not yet implemented");
        }

        if (context.func_arg_list() is { } funcArgList)
        {
            foreach (var argExpr in funcArgList.func_arg_expr())
            {
                if (argExpr.param_name() is not null)
                {
                    throw new NotImplementedException("Support for named parameters is not yet implemented");
                }

                if (VisitA_expr(argExpr.a_expr()) is not Expression expression)
                {
                    throw new PostgresParseException("Expected argument a_expr to return an Expression");
                }

                func.Arguments.Add(new FunctionArgument(expression));
            }
        }

        return func;
    }
}