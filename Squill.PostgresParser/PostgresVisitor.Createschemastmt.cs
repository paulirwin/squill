using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // createschemastmt
    //   : CREATE SCHEMA (IF_P NOT EXISTS)? (optschemaname? AUTHORIZATION rolespec | colid) optschemaeltlist
    // The schema itself is modeled; an AUTHORIZATION role is carried but not (issue #143),
    // since Squill does not manage roles. A non-constant role (CURRENT_USER / SESSION_USER) is
    // accepted wherever the schema has a name of its own (issue #166). Inline schema elements
    // (CREATE SCHEMA ... CREATE TABLE ...) remain unsupported.
    public override SyntaxNode VisitCreateschemastmt(PostgreSQLParser.CreateschemastmtContext context)
    {
        Identifier name;
        string? authorization = null;

        if (context.AUTHORIZATION() is not null)
        {
            // `CREATE SCHEMA AUTHORIZATION joe` — with no explicit name — creates a schema
            // named after the role, so the role doubles as the name. `CREATE SCHEMA s
            // AUTHORIZATION joe` names it explicitly via optschemaname.
            //
            // Which of the two it is decides whether a non-constant role is acceptable, so
            // the name is resolved first: only the name-less form has to turn the role into
            // a schema name, and only there is CURRENT_USER / SESSION_USER unusable
            // (issue #166).
            if (context.optschemaname()?.colid() is { } authorizedName)
            {
                if (VisitColid(authorizedName) is not Identifier explicitName)
                {
                    throw new PostgresParseException("Unable to parse schema name");
                }

                name = explicitName;

                // The schema's name is stable, so the role only decides ownership — which is
                // unmodeled either way (SQ1002, issue #143): the generated DDL is a bare
                // CREATE SCHEMA IF NOT EXISTS, with no AUTHORIZATION at all. A non-constant
                // role therefore costs nothing extra here. It is still carried as the token it
                // was written as, so the warning can name what was dropped.
                authorization = context.rolespec()?.GetText()
                    ?? throw new PostgresParseException(
                        "Unable to parse AUTHORIZATION role");
            }
            else
            {
                authorization = ParseNameGivingAuthorizationRole(context.rolespec());
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
    /// The role named by the name-less <c>CREATE SCHEMA AUTHORIZATION role</c>, where the role
    /// also supplies the schema's name. Only a plain role name is accepted here: CURRENT_USER
    /// and SESSION_USER resolve at execution time, so the resulting schema name is not knowable
    /// at build time — and a declarative model cannot contain an object whose name depends on
    /// who deploys it. Measured against <c>postgres:latest</c>, the same statement really does
    /// produce a different schema per deploying role, and the catalog keeps no trace of the
    /// token that produced it, so the name could not be recovered even at extraction time.
    ///
    /// <para>
    /// The named form (<c>CREATE SCHEMA s AUTHORIZATION CURRENT_USER</c>) does not come through
    /// here: its name is stable and only its ownership is deploy-resolved, which is already
    /// unmodeled, so it accepts any role spec (issue #166).
    /// </para>
    /// </summary>
    private static string ParseNameGivingAuthorizationRole(PostgreSQLParser.RolespecContext? context)
    {
        if (context?.nonreservedword() is not { } role)
        {
            throw new NotImplementedException(
                "AUTHORIZATION CURRENT_USER / SESSION_USER on a CREATE SCHEMA with no schema "
                + "name is not supported: the schema takes its name from the role, which "
                + "resolves at deploy time, so the name is not known at build time. Give the "
                + "schema an explicit name (CREATE SCHEMA <name> AUTHORIZATION CURRENT_USER), "
                + "or name the role explicitly.");
        }

        return role.GetText();
    }
}
