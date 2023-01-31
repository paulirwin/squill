using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitPlsqlvariablename(PostgreSQLParser.PlsqlvariablenameContext context)
    {
        var name = context.PLSQLVARIABLENAME().GetText().TrimStart(':');
        return new PLSQLVariableName(name);
    }
}