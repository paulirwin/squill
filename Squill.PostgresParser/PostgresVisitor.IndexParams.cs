using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitIndex_params(PostgreSQLParser.Index_paramsContext context)
    {
        var items = new List<IndexElement>();

        foreach (var indexElemContext in context.index_elem())
        {
            if (VisitIndex_elem(indexElemContext) is not IndexElement indexElement)
            {
                throw new PostgresParseException("Unable to parse index element");
            }

            items.Add(indexElement);
        }

        return new SyntaxList<IndexElement>(items);
    }
}