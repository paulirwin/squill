using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitRelation_expr(PostgreSQLParser.Relation_exprContext context)
    {
        if (VisitQualified_name(context.qualified_name()) is not QualifiedName qualifiedName)
        {
            throw new PostgresParseException("Unable to parse qualified name for relation expression");
        }

        bool star = context.STAR() is not null;
        bool only = context.ONLY() is not null;

        return new RelationExpression(qualifiedName, star, only);
    }
}