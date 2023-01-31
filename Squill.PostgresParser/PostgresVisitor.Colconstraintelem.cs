using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitColconstraintelem(PostgreSQLParser.ColconstraintelemContext context)
    {
        if (context.NULL_P() is not null)
        {
            return new NullableColumnConstraint(context.GetText(), context.NOT() is null);
        }

        if (context.PRIMARY() is not null && context.KEY() is not null)
        {
            // TODO: support opt_definition and optconsttablespace
            return new PrimaryKeyColumnConstraint(context.GetText());
        }

        if (context.DEFAULT() is not null)
        {
            if (VisitB_expr(context.b_expr()) is not Expression expression)
            {
                throw new PostgresParseException("Expected an Expression for DEFAULT constraint");
            }

            return new DefaultColumnConstraint(context.GetText(), expression);
        }

        // TODO: support UNIQUE, CHECK, DEFAULT, GENERATED, and REFERENCES
        throw new NotImplementedException("Column constraint type not yet implemented");
    }
}