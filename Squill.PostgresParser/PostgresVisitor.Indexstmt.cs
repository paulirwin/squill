using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitIndexstmt(PostgreSQLParser.IndexstmtContext context)
    {
        if (context.opt_include().INCLUDE() is not null)
        {
            throw new NotImplementedException("Support for INCLUDE on CREATE INDEX not yet implemented");
        }

        if (context.opt_reloptions().WITH() is not null)
        {
            throw new NotImplementedException("Support for WITH on CREATE INDEX not yet implemented");
        }

        if (context.opttablespace().TABLESPACE() is not null)
        {
            throw new NotImplementedException("Support for TABLESPACE on CREATE INDEX not yet implemented");
        }

        if (context.where_clause().WHERE() is not null)
        {
            throw new NotImplementedException("Support for WHERE on CREATE INDEX not yet implemented");
        }

        bool unique = context.opt_unique()?.UNIQUE() is not null;
        bool concurrently = context.opt_concurrently()?.CONCURRENTLY() is not null;
        bool ifNotExists = context.EXISTS() is not null;

        Identifier? name = null;

        if (ifNotExists)
        {
            if (VisitName(context.name()) is not Identifier ifNotExistsName)
            {
                throw new PostgresParseException("Unable to parse index name");
            }

            name = ifNotExistsName;
        }
        else if (context.opt_index_name()?.name() is { } nameContext)
        {
            if (VisitName(nameContext) is not Identifier optName)
            {
                throw new PostgresParseException("Unable to parse index name");
            }

            name = optName;
        }

        Identifier? usingMethod = null;

        if (context.access_method_clause() is { } accessMethodClause
            && accessMethodClause.USING() is not null)
        {
            if (VisitName(accessMethodClause.name()) is not Identifier usingName)
            {
                throw new PostgresParseException("Unable to parse USING method for index");
            }

            usingMethod = usingName;
        }

        if (VisitRelation_expr(context.relation_expr()) is not RelationExpression onRelation)
        {
            throw new PostgresParseException("Unable to parse relation expression for index");
        }

        var createIndex = new CreateIndexStatement(name, onRelation, unique, concurrently, ifNotExists, usingMethod);

        if (VisitIndex_params(context.index_params()) is not SyntaxList<IndexElement> elements)
        {
            throw new PostgresParseException("Unable to parse index element list");
        }

        createIndex.Elements.AddRange(elements.Items);

        return createIndex;
    }
}