using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // createdomainstmt : CREATE DOMAIN_P any_name opt_as typename colquallist
    // A domain is a base type plus a list of (optionally named) constraints — the same
    // colquallist grammar a table column uses — so the constraint iteration mirrors
    // VisitColumnDef.
    public override SyntaxNode VisitCreatedomainstmt(PostgreSQLParser.CreatedomainstmtContext context)
    {
        var name = ParseAnyName(context.any_name());

        if (VisitTypename(context.typename()) is not DataType dataType)
        {
            throw new PostgresParseException("Unable to parse domain base type");
        }

        var statement = At(new CreateDomainStatement(name, dataType), context);

        if (context.colquallist()?.colconstraint() is { Length: > 0 } colconstraints)
        {
            foreach (var colconstraint in colconstraints)
            {
                if (colconstraint.CONSTRAINT() is not null
                    && colconstraint.name() is { } nameContext)
                {
                    if (VisitColconstraintelem(colconstraint.colconstraintelem()) is not ColumnConstraint
                        innerConstraint)
                    {
                        throw new PostgresParseException(
                            "Expected VisitColconstraintelem to return a ColumnConstraint");
                    }

                    // TODO: support quoted identifiers properly instead of just nameContext.GetText()
                    statement.Constraints.Add(new NamedColumnConstraint(colconstraint.GetText(),
                        nameContext.GetText(), innerConstraint));
                }
                else if (colconstraint.colconstraintelem() is { } colconstraintelem)
                {
                    if (VisitColconstraintelem(colconstraintelem) is not ColumnConstraint columnConstraint)
                    {
                        throw new PostgresParseException(
                            "Expected VisitColconstraintelem to return a ColumnConstraint");
                    }

                    statement.Constraints.Add(columnConstraint);
                }
                else
                {
                    throw new NotImplementedException(
                        "CONSTRAINT attributes and COLLATE are not supported on a domain");
                }
            }
        }

        return statement;
    }
}
