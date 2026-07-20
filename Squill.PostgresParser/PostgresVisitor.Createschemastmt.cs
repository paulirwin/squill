using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // createschemastmt
    //   : CREATE SCHEMA (IF_P NOT EXISTS)? (optschemaname AUTHORIZATION rolespec | colid) optschemaeltlist
    // Only the common `CREATE SCHEMA [IF NOT EXISTS] name` form is supported; the
    // AUTHORIZATION variant and inline schema elements (CREATE SCHEMA ... CREATE TABLE ...)
    // are not yet modeled.
    public override SyntaxNode VisitCreateschemastmt(PostgreSQLParser.CreateschemastmtContext context)
    {
        if (context.AUTHORIZATION() is not null)
        {
            throw new NotImplementedException(
                "AUTHORIZATION on CREATE SCHEMA is not yet supported");
        }

        if (context.colid() is not { } colid)
        {
            throw new PostgresParseException("Unable to parse schema name");
        }

        if (VisitColid(colid) is not Identifier name)
        {
            throw new PostgresParseException("Unable to parse schema name");
        }

        if (context.optschemaeltlist()?.schema_stmt().Length > 0)
        {
            throw new NotImplementedException(
                "Inline schema elements on CREATE SCHEMA (CREATE SCHEMA ... CREATE TABLE ...) "
                + "are not yet supported; declare the schema and its objects separately");
        }

        var ifNotExists = context.EXISTS() is not null;

        return new CreateSchemaStatement(name, ifNotExists);
    }
}
