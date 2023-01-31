using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitTableconstraint(PostgreSQLParser.TableconstraintContext context)
    {
        if (VisitConstraintelem(context.constraintelem()) is not TableConstraint constraint)
        {
            throw new PostgresParseException("Unable to parse table constraint element");
        }

        if (context.CONSTRAINT() is not null
            && context.name() is { } nameContext)
        {
            if (VisitColid(nameContext.colid()) is not Identifier nameIdentifier)
            {
                throw new PostgresParseException("Unable to parse named constraint identifier");
            }

            return new NamedTableConstraint(nameIdentifier, constraint);
        }

        return constraint;
    }
}