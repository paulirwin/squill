using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // definestmt covers many DEFINE-family objects (AGGREGATE, OPERATOR, TYPE, TEXT SEARCH,
    // COLLATION). The modeled CREATE TYPE forms are AS ENUM, AS (...) (composite) and
    // AS RANGE (...) — see issue #122. Shell types, base types, operators, text-search
    // objects and collations are reported as unsupported rather than silently dropped.
    public override SyntaxNode VisitDefinestmt(PostgreSQLParser.DefinestmtContext context)
    {
        if (context.AGGREGATE() is not null)
        {
            return VisitCreateAggregate(context);
        }

        if (context.TYPE_P() is not null)
        {
            if (context.ENUM_P() is not null)
            {
                return VisitCreateEnumType(context);
            }

            if (context.RANGE() is not null)
            {
                return VisitCreateRangeType(context);
            }

            // `CREATE TYPE name AS ( ... )`. The composite alternative is the only remaining
            // one carrying an AS with a parenthesized element list; a bare `CREATE TYPE name`
            // (shell) or `CREATE TYPE name (...)` (base type) has no AS and falls through.
            if (context.AS() is not null)
            {
                return VisitCreateCompositeType(context);
            }

            throw new NotImplementedException(
                "Only CREATE TYPE ... AS ENUM, AS (...) and AS RANGE are supported; a shell "
                + "type or a base type (CREATE TYPE name (INPUT = ..., OUTPUT = ...)) is not");
        }

        throw new NotImplementedException(
            "Only CREATE TYPE and CREATE AGGREGATE are supported among the DEFINE-family "
            + "statements");
    }

    // definestmt: CREATE TYPE_P any_name AS OPEN_PAREN opttablefuncelementlist CLOSE_PAREN
    // tablefuncelement : colid typename opt_collate_clause — so each attribute is a name and a
    // type, parsed exactly as a table column's would be. Attribute order is significant (it is
    // the field order of the type's row values), so the declared order is preserved.
    private CreateCompositeTypeStatement VisitCreateCompositeType(
        PostgreSQLParser.DefinestmtContext context)
    {
        var statement = At(
            new CreateCompositeTypeStatement(ParseAnyName(context.any_name()[0])), context);

        var elementList = context.opttablefuncelementlist()?.tablefuncelementlist();

        if (elementList is null)
        {
            // `CREATE TYPE name AS ()` — PostgreSQL allows an attribute-less composite type.
            return statement;
        }

        foreach (var element in elementList.tablefuncelement())
        {
            if (VisitColid(element.colid()) is not Identifier attributeName)
            {
                throw new PostgresParseException("Unable to parse composite type attribute name");
            }

            if (VisitTypename(element.typename()) is not DataType dataType)
            {
                throw new PostgresParseException(
                    $"Unable to parse the type of composite type attribute '{attributeName.Name}'");
            }

            statement.Attributes.Add(
                At(new CompositeTypeAttribute(attributeName, dataType), element));
        }

        return statement;
    }

    // definestmt: CREATE TYPE_P any_name AS RANGE definition
    // The definition is the same name=value def_list an aggregate uses. SUBTYPE is required —
    // it is what gives the range its identity — and the rest are optional refinements.
    private CreateRangeTypeStatement VisitCreateRangeType(PostgreSQLParser.DefinestmtContext context)
    {
        var name = ParseAnyName(context.any_name()[0]);

        DataType? subtype = null;
        string? operatorClass = null;
        string? collation = null;

        foreach (var defElem in context.definition().def_list().def_elem())
        {
            switch (defElem.colLabel().GetText().ToUpperInvariant())
            {
                case "SUBTYPE":
                    subtype = ParseRangeSubtype(defElem);
                    break;
                case "SUBTYPE_OPCLASS":
                    operatorClass = DefArgText(defElem).Trim('"').ToLowerInvariant();
                    break;
                case "COLLATION":
                    // A collation name is case-sensitive and conventionally quoted ("C"), so
                    // the quotes are stripped but the case is kept.
                    collation = DefArgText(defElem).Trim('"');
                    break;
                // CANONICAL, SUBTYPE_DIFF and MULTIRANGE_TYPE_NAME are recognized but not
                // modeled: they reference functions or an implicitly created companion type,
                // neither of which takes part in the range type's identity here.
            }
        }

        if (subtype is null)
        {
            throw new PostgresParseException(
                "A CREATE TYPE ... AS RANGE must declare a SUBTYPE");
        }

        var statement = At(new CreateRangeTypeStatement(name, subtype), context)
            as CreateRangeTypeStatement;

        statement!.SubtypeOperatorClass = operatorClass;
        statement.Collation = collation;

        return statement;
    }

    // SUBTYPE is a type name; parsing it through the typename visitor yields the same DataType
    // a column would get, so the model builder normalizes it identically.
    private DataType ParseRangeSubtype(PostgreSQLParser.Def_elemContext context)
    {
        if (context.def_arg()?.func_type()?.typename() is not { } typename
            || VisitTypename(typename) is not DataType dataType)
        {
            throw new PostgresParseException("Unable to parse the range type's SUBTYPE");
        }

        return dataType;
    }

    // definestmt (aggregate alternatives):
    //   CREATE opt_or_replace AGGREGATE func_name aggr_args definition
    //   CREATE opt_or_replace AGGREGATE func_name old_aggr_definition
    // Only the modern form (aggr_args + definition) is modeled: the input types come from
    // aggr_args and the SFUNC/STYPE items from the definition's def_list.
    private CreateAggregateStatement VisitCreateAggregate(PostgreSQLParser.DefinestmtContext context)
    {
        if (context.old_aggr_definition() is not null)
        {
            throw new NotImplementedException(
                "The old-style CREATE AGGREGATE (name, ...) syntax is not supported; "
                + "use CREATE AGGREGATE name(argtypes) (SFUNC = ..., STYPE = ...)");
        }

        var name = ParseFunctionName(context.func_name());

        var statement = At(new CreateAggregateStatement(name, context.or_replace_()?.REPLACE() is not null),
            context);

        foreach (var parameter in ParseAggregateArguments(context.aggr_args()))
        {
            statement.Parameters.Add(parameter);
        }

        ApplyAggregateDefinition(statement, context.definition());

        if (statement.StateFunction is null)
        {
            throw new PostgresParseException("A CREATE AGGREGATE must declare an SFUNC");
        }

        if (statement.StateType is null)
        {
            throw new PostgresParseException("A CREATE AGGREGATE must declare an STYPE");
        }

        return statement;
    }

    // definition : OPEN_PAREN def_list CLOSE_PAREN ; def_list : def_elem (COMMA def_elem)*
    // def_elem : collabel (EQUAL def_arg)? — the aggregate's items are name=value pairs.
    // SFUNC and STYPE are the two Squill models; any other item is recognized and skipped.
    private void ApplyAggregateDefinition(CreateAggregateStatement statement,
        PostgreSQLParser.DefinitionContext context)
    {
        foreach (var defElem in context.def_list().def_elem())
        {
            var itemName = defElem.colLabel().GetText().ToUpperInvariant();

            switch (itemName)
            {
                case "SFUNC":
                    statement.StateFunction = ParseAggregateNameArg(defElem);
                    break;
                case "STYPE":
                    statement.StateType = ParseAggregateTypeArg(defElem);
                    break;
                // FINALFUNC, INITCOND, SORTOP, PARALLEL, COMBINEFUNC, etc. are recognized but
                // not modeled — the aggregate's identity is its name, input types, SFUNC and
                // STYPE.
            }
        }
    }

    // SFUNC is a (possibly schema-qualified) function name. It arrives as a def_arg wrapping a
    // func_type/typename; the text is taken verbatim from the source span so a qualified name
    // survives, and folded to lower case as Postgres folds an unquoted identifier.
    private static string ParseAggregateNameArg(PostgreSQLParser.Def_elemContext context)
        => DefArgText(context).ToLowerInvariant();

    // STYPE is a type name. Parsing it through the typename visitor yields the same DataType a
    // column or return type would, so the model builder normalizes it identically (and an
    // array STYPE like numeric[] is handled).
    private DataType ParseAggregateTypeArg(PostgreSQLParser.Def_elemContext context)
    {
        if (context.def_arg()?.func_type()?.typename() is not { } typename)
        {
            throw new PostgresParseException("Unable to parse the CREATE AGGREGATE STYPE");
        }

        if (VisitTypename(typename) is not DataType dataType)
        {
            throw new PostgresParseException("Unable to parse the CREATE AGGREGATE STYPE");
        }

        return dataType;
    }

    private static string DefArgText(PostgreSQLParser.Def_elemContext context)
    {
        if (context.def_arg() is not { } defArg)
        {
            throw new PostgresParseException(
                $"The CREATE AGGREGATE item '{context.colLabel().GetText()}' requires a value");
        }

        return defArg.Start.InputStream.GetText(
            Antlr4.Runtime.Misc.Interval.Of(defArg.Start.StartIndex, defArg.Stop.StopIndex));
    }

    // aggr_args : OPEN_PAREN (STAR | aggr_args_list | ORDER BY aggr_args_list
    //                         | aggr_args_list ORDER BY aggr_args_list) CLOSE_PAREN
    // aggr_arg : func_arg — so each aggregate input type reuses the routine-parameter parser.
    // An aggregate over `*` (like count(*)) has no argument list.
    private IEnumerable<RoutineParameter> ParseAggregateArguments(PostgreSQLParser.Aggr_argsContext context)
    {
        if (context.STAR() is not null)
        {
            yield break;
        }

        var lists = context.aggr_args_list();

        if (lists.Length > 1 || context.ORDER() is not null)
        {
            throw new NotImplementedException(
                "An ordered-set aggregate (WITHIN GROUP / ORDER BY direct args) is not supported");
        }

        if (lists.Length == 0)
        {
            yield break;
        }

        foreach (var aggrArg in lists[0].aggr_arg())
        {
            yield return At(ParseParameter(aggrArg.func_arg()), aggrArg);
        }
    }

    private CreateEnumTypeStatement VisitCreateEnumType(PostgreSQLParser.DefinestmtContext context)
    {
        // The enum alternative is `CREATE TYPE_P any_name AS ENUM_P ( opt_enum_val_list )`,
        // so any_name() yields a single name here.
        var name = ParseAnyName(context.any_name()[0]);

        var labels = new List<string>();

        if (context.enum_val_list_()?.enum_val_list() is { } enumValList)
        {
            foreach (var sconst in enumValList.sconst())
            {
                if (VisitSconst(sconst) is not LiteralExpression { Value: string label })
                {
                    throw new PostgresParseException("Unable to parse enum label");
                }

                labels.Add(label);
            }
        }

        return At(new CreateEnumTypeStatement(name, labels), context);
    }

    // any_name : colid attrs?  where attrs : (DOT attr_name)+
    // Parses a (possibly schema-qualified) object name into a QualifiedName, mirroring how
    // qualified_name and func_name are parsed elsewhere.
    private QualifiedName ParseAnyName(PostgreSQLParser.Any_nameContext context)
    {
        if (VisitColid(context.colid()) is not Identifier first)
        {
            throw new PostgresParseException("Unable to parse type name");
        }

        var segments = new List<Identifier> { first };

        if (context.attrs() is { } attrs)
        {
            foreach (var attrName in attrs.attr_name())
            {
                segments.Add(ParseNameSegment(attrName.colLabel(), attrName));
            }
        }

        return new QualifiedName(segments);
    }
}
