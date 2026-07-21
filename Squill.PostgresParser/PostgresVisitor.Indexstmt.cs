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

        if (context.opttablespace().TABLESPACE() is not null)
        {
            throw new NotImplementedException("Support for TABLESPACE on CREATE INDEX not yet implemented");
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

        var createIndex = At(new CreateIndexStatement(name, onRelation, unique, concurrently, ifNotExists, usingMethod), context);

        if (VisitIndex_params(context.index_params()) is not SyntaxList<IndexElement> elements)
        {
            throw new PostgresParseException("Unable to parse index element list");
        }

        createIndex.Elements.AddRange(elements.Items);

        // A WHERE clause makes this a partial (filtered) index; parse its predicate.
        if (context.where_clause() is { } whereClause && whereClause.WHERE() is not null)
        {
            if (Visit(whereClause.a_expr()) is not Expression predicate)
            {
                throw new PostgresParseException("Unable to parse WHERE predicate for index");
            }

            createIndex.WhereClause = predicate;
        }

        // WITH (...) storage parameters, e.g. HNSW's m / ef_construction. Each element is
        // a name with an optional value; the value text is captured verbatim.
        if (context.opt_reloptions()?.reloptions()?.reloption_list() is { } reloptionList)
        {
            foreach (var reloptionElem in reloptionList.reloption_elem())
            {
                // reloption_elem: collabel (EQUAL def_arg | DOT collabel (EQUAL def_arg)?)?
                // Only the simple "name = value" form is supported (no namespace.qualifier).
                if (reloptionElem.DOT() is not null)
                {
                    throw new NotImplementedException(
                        "Namespaced index storage parameters (namespace.option) are not yet supported");
                }

                var optionName = reloptionElem.collabel(0).GetText();
                var optionValue = reloptionElem.def_arg()?.GetText();

                createIndex.WithOptions.Add(new IndexWithOption(optionName, optionValue));
            }
        }

        return createIndex;
    }
}