using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // createschemastmt
    //   : CREATE SCHEMA (IF_P NOT EXISTS)? (optschemaname? AUTHORIZATION rolespec | colid) optschemaeltlist
    // The schema itself is modeled; an AUTHORIZATION role is carried but not (issue #143),
    // since Squill does not manage roles. Inline schema elements
    // (CREATE SCHEMA ... CREATE TABLE ...) remain unsupported.
    public override SyntaxNode VisitCreateschemastmt(PostgreSQLParser.CreateschemastmtContext context)
    {
        Identifier name;
        string? authorization = null;

        if (context.AUTHORIZATION() is not null)
        {
            authorization = ParseSchemaAuthorizationRole(context.rolespec());

            // `CREATE SCHEMA AUTHORIZATION joe` — with no explicit name — creates a schema
            // named after the role, so the role doubles as the name. `CREATE SCHEMA s
            // AUTHORIZATION joe` names it explicitly via optschemaname.
            if (context.optschemaname()?.colid() is { } authorizedName)
            {
                if (VisitColid(authorizedName) is not Identifier explicitName)
                {
                    throw new PostgresParseException("Unable to parse schema name");
                }

                name = explicitName;
            }
            else
            {
                name = new SimpleIdentifier(authorization);
            }
        }
        else
        {
            if (context.colid() is not { } colid)
            {
                throw new PostgresParseException("Unable to parse schema name");
            }

            if (VisitColid(colid) is not Identifier parsedName)
            {
                throw new PostgresParseException("Unable to parse schema name");
            }

            name = parsedName;
        }

        if (context.optschemaeltlist()?.schema_stmt().Length > 0)
        {
            throw new NotImplementedException(
                "Inline schema elements on CREATE SCHEMA (CREATE SCHEMA ... CREATE TABLE ...) "
                + "are not yet supported; declare the schema and its objects separately");
        }

        var ifNotExists = context.EXISTS() is not null;

        return At(new CreateSchemaStatement(name, ifNotExists)
        {
            Authorization = authorization,
        }, context);
    }

    /// <summary>
    /// The role named by <c>CREATE SCHEMA ... AUTHORIZATION</c>. Only a plain role name is
    /// accepted: CURRENT_USER and SESSION_USER resolve at execution time, so in the name-less
    /// form the resulting schema name is not knowable at build time — and a declarative model
    /// cannot contain an object whose name depends on who deploys it.
    /// </summary>
    private static string ParseSchemaAuthorizationRole(PostgreSQLParser.RolespecContext? context)
    {
        if (context?.nonreservedword() is not { } role)
        {
            throw new NotImplementedException(
                "AUTHORIZATION CURRENT_USER / SESSION_USER on CREATE SCHEMA is not supported: "
                + "the role resolves at deploy time, so the schema it names is not known at "
                + "build time. Name the role explicitly.");
        }

        return role.GetText();
    }
}
