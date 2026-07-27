using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // a_expr_at_time_zone : a_expr_collate (AT TIME ZONE a_expr)?
    //
    // As with the caret rule, the zone operand is a full a_expr and so over-captures:
    // `c AT TIME ZONE 'UTC' > d` arrives with the comparison folded into the zone. Postgres
    // binds AT TIME ZONE tighter, so the operand is rebalanced back.
    public override SyntaxNode VisitA_expr_at_time_zone(PostgreSQLParser.A_expr_at_time_zoneContext context)
    {
        var expression = VisitA_expr_collate(context.a_expr_collate());

        if (context.AT() is null)
        {
            return expression;
        }

        if (expression is not Expression operand)
        {
            throw new PostgresParseException("Unable to parse AT TIME ZONE expression operand");
        }

        if (VisitA_expr(context.a_expr()) is not Expression timeZone)
        {
            throw new PostgresParseException("Unable to parse AT TIME ZONE zone operand");
        }

        return RebalanceRightOperand(
            timeZone,
            zone => new AtTimeZoneExpression(operand, zone));
    }
}
