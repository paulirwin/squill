using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    public override SyntaxNode VisitIndexstmt(PostgreSQLParser.IndexstmtContext context)
    {
        bool unique = context.unique_()?.UNIQUE() is not null;
        bool concurrently = context.concurrently_()?.CONCURRENTLY() is not null;
        bool ifNotExists = context.if_not_exists_() is not null;

        Identifier? name = null;

        if (ifNotExists)
        {
            if (VisitName(context.name()) is not Identifier ifNotExistsName)
            {
                throw new PostgresParseException("Unable to parse index name");
            }

            name = ifNotExistsName;
        }
        else if (context.index_name_()?.name() is { } nameContext)
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

        // These clauses are optional in the grammar, so an absent one is a null context rather
        // than a rule matching empty.

        // INCLUDE (...) covering columns. The grammar reuses index_elem for them, so they parse
        // through the same visitor as the key columns (issue #160).
        if (context.include_()?.index_including_params() is { } includingParams)
        {
            foreach (var includeElem in includingParams.index_elem())
            {
                if (Visit(includeElem) is not IndexElement includeElement)
                {
                    throw new PostgresParseException("Unable to parse INCLUDE column for index");
                }

                createIndex.IncludeElements.Add(includeElement);
            }
        }

        // NULLS NOT DISTINCT (PostgreSQL 15+). nulls_distinct : NULLS_P NOT? DISTINCT, so the
        // NOT is what separates it from NULLS DISTINCT, the explicit spelling of the default.
        if (context.nulls_distinct() is { } nullsDistinct)
        {
            createIndex.NullsNotDistinct = nullsDistinct.NOT() is not null;
        }

        if (context.opttablespace()?.name() is { } tablespaceName)
        {
            if (VisitName(tablespaceName) is not Identifier tablespace)
            {
                throw new PostgresParseException("Unable to parse TABLESPACE for index");
            }

            createIndex.TableSpace = tablespace;
        }

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
        if (context.reloptions_()?.reloptions()?.reloption_list() is { } reloptionList)
        {
            AddStorageParameters(reloptionList, createIndex.WithOptions);
        }

        return createIndex;
    }

    /// <summary>
    /// Reads a <c>reloptions</c> list into <paramref name="options"/>. Shared by <c>CREATE
    /// INDEX</c> and <c>CREATE TABLE</c> (issue #206), which reach the same grammar rule.
    /// </summary>
    private static void AddStorageParameters(
        PostgreSQLParser.Reloption_listContext context, IList<IndexWithOption> options)
    {
        foreach (var reloptionElem in context.reloption_elem())
        {
            // reloption_elem : colLabel (EQUAL def_arg | DOT colLabel (EQUAL def_arg)?)?
            //
            // The DOT alternative is a namespaced parameter -- `toast.autovacuum_enabled` and the
            // rest of the toast.* family, which a table may well declare. Its two labels are
            // rejoined into the one dotted name the catalog itself stores, rather than being
            // refused: dropping the namespace half would silently turn a TOAST-relation setting
            // into a same-named setting on the table.
            var optionName = reloptionElem.DOT() is not null
                ? $"{reloptionElem.colLabel(0).GetText()}.{reloptionElem.colLabel(1).GetText()}"
                : reloptionElem.colLabel(0).GetText();

            options.Add(new IndexWithOption(optionName, reloptionElem.def_arg()?.GetText()));
        }
    }
}