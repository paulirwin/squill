using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // createfunctionstmt
    //   : CREATE opt_or_replace (FUNCTION | PROCEDURE) func_name func_args_with_defaults
    //     (RETURNS (func_return | TABLE OPEN_PAREN table_func_column_list CLOSE_PAREN))?
    //     createfunc_opt_list
    //
    // FUNCTION and PROCEDURE share this rule. They differ in that a function has a RETURNS
    // clause and volatility/strictness attributes; both are parsed here.
    public override SyntaxNode VisitCreatefunctionstmt(PostgreSQLParser.CreatefunctionstmtContext context)
    {
        if (context.PROCEDURE() is null)
        {
            return VisitCreateFunction(context);
        }

        var name = ParseFunctionName(context.func_name());

        var statement = At(new CreateProcedureStatement(name, context.or_replace_()?.REPLACE() is not null),
            context);

        foreach (var parameter in ParseParameters(context.func_args_with_defaults()))
        {
            statement.Parameters.Add(parameter);
        }

        ApplyOptions(statement, context.createfunc_opt_list());

        if (statement.Language is null)
        {
            throw new NotImplementedException(
                "A procedure without a LANGUAGE clause is not supported");
        }

        if (statement.Body is null)
        {
            throw new NotImplementedException(
                "A procedure without an AS body is not supported; "
                + "linked C-language procedures are not modeled");
        }

        return statement;
    }

    private CreateFunctionStatement VisitCreateFunction(PostgreSQLParser.CreatefunctionstmtContext context)
    {
        var name = ParseFunctionName(context.func_name());

        var statement = At(new CreateFunctionStatement(name, context.or_replace_()?.REPLACE() is not null),
            context);

        foreach (var parameter in ParseParameters(context.func_args_with_defaults()))
        {
            statement.Parameters.Add(parameter);
        }

        ParseFunctionReturn(statement, context);
        ApplyFunctionOptions(statement, context.createfunc_opt_list());

        if (statement.Language is null)
        {
            throw new NotImplementedException(
                "A function without a LANGUAGE clause is not supported");
        }

        if (statement.Body is null)
        {
            throw new NotImplementedException(
                "A function without an AS body is not supported; "
                + "linked C-language functions are not modeled");
        }

        return statement;
    }

    // RETURNS (func_return | TABLE OPEN_PAREN table_func_column_list CLOSE_PAREN)
    // func_return : func_type ; func_type : typename | ... %TYPE.  A `RETURNS SETOF x` carries
    // its SETOF token on the typename itself (typename : SETOF? simpletypename ...).
    private void ParseFunctionReturn(CreateFunctionStatement statement,
        PostgreSQLParser.CreatefunctionstmtContext context)
    {
        if (context.RETURNS() is null)
        {
            // A function with no RETURNS is unusual (only OUT parameters define the result);
            // not supported yet.
            throw new NotImplementedException(
                "A function without a RETURNS clause is not yet supported");
        }

        if (context.TABLE() is not null)
        {
            throw new NotImplementedException(
                "RETURNS TABLE(...) is not yet supported");
        }

        if (context.func_return()?.func_type() is not { } funcType)
        {
            throw new PostgresParseException("Unable to parse function return type");
        }

        if (funcType.typename() is not { } typename)
        {
            throw new NotImplementedException(
                "A %TYPE function return type is not yet supported");
        }

        if (VisitTypename(typename) is not DataType returnType)
        {
            throw new PostgresParseException("Unable to parse function return type");
        }

        statement.ReturnType = returnType;
        statement.ReturnsSet = typename.SETOF() is not null;
    }

    private void ApplyFunctionOptions(CreateFunctionStatement statement,
        PostgreSQLParser.Createfunc_opt_listContext context)
    {
        foreach (var option in context.createfunc_opt_item())
        {
            if (option.LANGUAGE() is not null)
            {
                statement.Language = GetNonReservedWordOrSconstText(option.nonreservedword_or_sconst());
                continue;
            }

            if (option.func_as() is { } funcAs)
            {
                if (funcAs.sconst().Length > 1)
                {
                    throw new NotImplementedException(
                        "A linked C-language function (AS 'obj_file', 'link_symbol') is not supported");
                }

                statement.Body = GetRoutineBodyText(funcAs.sconst(0));
                continue;
            }

            if (option.common_func_opt_item() is { } commonOption)
            {
                ApplyFunctionCommonOption(statement, commonOption);
                continue;
            }

            if (option.WINDOW() is not null)
            {
                throw new NotImplementedException("WINDOW functions are not yet supported");
            }

            if (option.TRANSFORM() is not null)
            {
                throw new NotImplementedException(
                    "TRANSFORM on CREATE FUNCTION is not yet supported");
            }
        }
    }

    // Unlike a procedure, a function's volatility (IMMUTABLE/STABLE/VOLATILE) and strictness
    // (STRICT / RETURNS NULL ON NULL INPUT vs CALLED ON NULL INPUT) are meaningful and modeled.
    private static void ApplyFunctionCommonOption(CreateFunctionStatement statement,
        PostgreSQLParser.Common_func_opt_itemContext context)
    {
        if (context.SECURITY() is not null)
        {
            statement.SecurityDefiner = context.DEFINER() is not null;
            return;
        }

        if (context.IMMUTABLE() is not null)
        {
            statement.Volatility = FunctionVolatility.Immutable;
            return;
        }

        if (context.STABLE() is not null)
        {
            statement.Volatility = FunctionVolatility.Stable;
            return;
        }

        if (context.VOLATILE() is not null)
        {
            statement.Volatility = FunctionVolatility.Volatile;
            return;
        }

        // STRICT and RETURNS NULL ON NULL INPUT are synonyms; CALLED ON NULL INPUT is the
        // default (non-strict).
        if (context.STRICT_P() is not null
            || (context.RETURNS() is not null && context.NULL_P() is not null))
        {
            statement.Strict = true;
            return;
        }

        if (context.CALLED() is not null)
        {
            statement.Strict = false;
            return;
        }

        // LEAKPROOF, COST, ROWS, SUPPORT, PARALLEL are accepted but not modeled (they are
        // planner hints that do not change the function's result). A SET clause does affect
        // execution and is not yet supported.
        if (context.functionsetresetclause() is not null)
        {
            throw new NotImplementedException(
                "A SET clause on CREATE FUNCTION is not yet supported");
        }
    }

    private IEnumerable<RoutineParameter> ParseParameters(
        PostgreSQLParser.Func_args_with_defaultsContext context)
    {
        var list = context.func_args_with_defaults_list();

        if (list is null)
        {
            yield break;
        }

        foreach (var argContext in list.func_arg_with_default())
        {
            var parameter = ParseParameter(argContext.func_arg());

            // A DEFAULT affects how the procedure may be called, not its identity, so the
            // expression is carried as source text rather than modeled.
            if (argContext.a_expr() is { } defaultExpression)
            {
                // Take the original source span rather than GetText(), which concatenates
                // tokens and would drop the whitespace between them.
                parameter.DefaultExpression = defaultExpression.Start.InputStream.GetText(
                    Antlr4.Runtime.Misc.Interval.Of(
                        defaultExpression.Start.StartIndex,
                        defaultExpression.Stop.StopIndex));
            }

            yield return At(parameter, argContext);
        }
    }

    // func_arg
    //   : arg_class param_name? func_type
    //   | param_name arg_class? func_type
    //   | func_type
    private RoutineParameter ParseParameter(PostgreSQLParser.Func_argContext context)
    {
        if (context.func_type() is not { } funcType)
        {
            throw new PostgresParseException("Unable to parse procedure parameter type");
        }

        if (funcType.typename() is not { } typename)
        {
            throw new NotImplementedException(
                "A %TYPE procedure parameter is not yet supported");
        }

        if (VisitTypename(typename) is not DataType dataType)
        {
            throw new PostgresParseException("Unable to parse procedure parameter type");
        }

        var name = context.param_name() is { } paramName ? ParseParameterName(paramName) : null;

        return new RoutineParameter(name, ParseParameterMode(context.arg_class()), dataType);
    }

    // func_name : builtin_function_name | type_function_name | colid indirection
    // The dotted form (schema.procedure) arrives as a colid plus an indirection of
    // .attr_name elements, exactly as for a qualified table name.
    private QualifiedName ParseFunctionName(PostgreSQLParser.Func_nameContext context)
    {
        if (context.colid() is { } colid)
        {
            if (VisitColid(colid) is not Identifier first)
            {
                throw new PostgresParseException("Unable to parse procedure name");
            }

            var segments = new List<Identifier> { first };

            if (context.indirection() is { } indirection)
            {
                foreach (var element in indirection.indirection_el())
                {
                    if (element.DOT() is null || element.attr_name() is not { } attrName)
                    {
                        throw new NotImplementedException(
                            "Only dotted qualifiers are supported in a procedure name");
                    }

                    segments.Add(ParseNameSegment(attrName.colLabel(), attrName));
                }
            }

            return new QualifiedName(segments);
        }

        if (context.type_function_name() is { } typeFunctionName)
        {
            return new QualifiedName([ParseTypeFunctionName(typeFunctionName)]);
        }

        throw new NotImplementedException(
            "A built-in function name may not be used as a procedure name");
    }

    private Identifier ParseParameterName(PostgreSQLParser.Param_nameContext context)
    {
        if (context.type_function_name() is { } typeFunctionName)
        {
            return ParseTypeFunctionName(typeFunctionName);
        }

        // A builtin_function_name (e.g. `abs`) is a valid parameter name; it is always a
        // bare keyword token, so fold it as Postgres folds an unquoted identifier.
        return new SimpleIdentifier(context.GetText().ToLowerInvariant());
    }

    // A type_function_name is either a real identifier (possibly quoted) or a keyword
    // usable as a name. Parse the identifier form for correct quote handling; fold the
    // keyword form to lower case as Postgres does for an unquoted identifier.
    private Identifier ParseTypeFunctionName(PostgreSQLParser.Type_function_nameContext context)
        => context.identifier() is { } identifier && VisitIdentifier(identifier) is Identifier parsed
            ? parsed
            : new SimpleIdentifier(context.GetText().ToLowerInvariant());

    private Identifier ParseNameSegment(
        PostgreSQLParser.ColLabelContext? collabel,
        PostgreSQLParser.Attr_nameContext attrName)
        => collabel?.identifier() is { } identifier && VisitIdentifier(identifier) is Identifier parsed
            ? parsed
            : new SimpleIdentifier((collabel?.GetText() ?? attrName.GetText()).ToLowerInvariant());

    // arg_class : IN_P OUT_P? | OUT_P | INOUT | VARIADIC
    // IN is PostgreSQL's default when no mode is written. `IN OUT` is a spelling of INOUT.
    private static ParameterMode ParseParameterMode(PostgreSQLParser.Arg_classContext? context)
    {
        if (context is null)
        {
            return ParameterMode.In;
        }

        if (context.INOUT() is not null)
        {
            return ParameterMode.InOut;
        }

        if (context.VARIADIC() is not null)
        {
            return ParameterMode.Variadic;
        }

        if (context.IN_P() is not null)
        {
            return context.OUT_P() is not null ? ParameterMode.InOut : ParameterMode.In;
        }

        return ParameterMode.Out;
    }

    private void ApplyOptions(
        CreateProcedureStatement statement,
        PostgreSQLParser.Createfunc_opt_listContext context)
    {
        foreach (var option in context.createfunc_opt_item())
        {
            if (option.LANGUAGE() is not null)
            {
                statement.Language = GetNonReservedWordOrSconstText(option.nonreservedword_or_sconst());
                continue;
            }

            if (option.func_as() is { } funcAs)
            {
                // The two-string form (AS 'obj_file', 'link_symbol') declares a procedure
                // implemented in a linked C library, which has no body Squill can model.
                if (funcAs.sconst().Length > 1)
                {
                    throw new NotImplementedException(
                        "A linked C-language procedure (AS 'obj_file', 'link_symbol') is not supported");
                }

                // Read the body verbatim: this is exactly the text PostgreSQL stores in
                // pg_proc.prosrc, so no canonicalization is needed for the parsed and
                // extracted models to agree.
                statement.Body = GetRoutineBodyText(funcAs.sconst(0));
                continue;
            }

            if (option.common_func_opt_item() is { } commonOption)
            {
                ApplyCommonOption(statement, commonOption);
                continue;
            }

            if (option.WINDOW() is not null)
            {
                throw new NotImplementedException("WINDOW is not valid on a procedure");
            }

            if (option.TRANSFORM() is not null)
            {
                throw new NotImplementedException(
                    "TRANSFORM on CREATE PROCEDURE is not yet supported");
            }
        }
    }

    /// <summary>
    /// Returns the text of a routine body string constant with its delimiters removed and
    /// escapes resolved. A dollar-quoted body ($$ … $$) is returned exactly as written —
    /// dollar quoting has no escapes — which is what PostgreSQL stores in pg_proc.prosrc.
    /// </summary>
    private static string GetRoutineBodyText(PostgreSQLParser.SconstContext context)
    {
        var anysconst = context.anysconst();

        if (anysconst.StringConstant() is { } stringConstant)
        {
            // A doubled quote inside a single-quoted body is an escaped quote.
            return TrimDelimiters(stringConstant.GetText()).Replace("''", "'");
        }

        if (anysconst.UnicodeEscapeStringConstant() is { } unicodeEscape)
        {
            return TrimDelimiters(unicodeEscape.GetText());
        }

        if (anysconst.EscapeStringConstant() is { } escapeConstant)
        {
            return TrimDelimiters(escapeConstant.GetText());
        }

        return string.Concat(anysconst.DollarText().Select(i => i.GetText()));
    }

    private static string TrimDelimiters(string text)
    {
        var start = text.IndexOf('\'');

        return start < 0 || text.Length < start + 2
            ? text
            : text[(start + 1)..^1];
    }

    private static void ApplyCommonOption(
        CreateProcedureStatement statement,
        PostgreSQLParser.Common_func_opt_itemContext context)
    {
        if (context.SECURITY() is not null)
        {
            statement.SecurityDefiner = context.DEFINER() is not null;
            return;
        }

        // Volatility, strictness, cost and parallel safety are accepted by the grammar on a
        // procedure but PostgreSQL ignores them for one (they describe how a function may be
        // optimized in an expression, and a procedure is never called from one), so they are
        // not modeled. SET clauses do affect execution and are not yet supported.
        if (context.functionsetresetclause() is not null)
        {
            throw new NotImplementedException(
                "A SET clause on CREATE PROCEDURE is not yet supported");
        }
    }
}
