using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitQualified_name(PostgreSQLParser.Qualified_nameContext context)
    {
        if (VisitColid(context.colid()) is not Identifier first)
        {
            throw new PostgresParseException("Unable to parse qualified name identifier");
        }

        var segments = new List<Identifier>
        {
            first
        };

        if (context.indirection() is not null)
        {
            throw new NotImplementedException("Dotted qualified names are not yet supported");
        }

        return new QualifiedName(segments);
    }
}