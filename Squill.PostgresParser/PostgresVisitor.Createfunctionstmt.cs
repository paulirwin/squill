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

        // A body is only absent for the SQL-standard BEGIN ATOMIC form, which the grammar
        // has no rule for (issue #213) — such a declaration fails as a syntax error before
        // reaching here — so anything that arrives without one is genuinely bodyless.
        if (statement.Body is null)
        {
            throw new NotImplementedException(
                "A procedure without an AS body is not supported");
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

        // The only standard form with no AS body is BEGIN ATOMIC, which the vendored
        // grammar has no rule for (issue #213); such a declaration fails as a syntax error
        // before reaching here, so anything arriving without a body is genuinely bodyless.
        if (statement.Body is null)
        {
            throw new NotImplementedException(
                "A function without an AS body is not supported");
        }

        return statement;
    }

    // RETURNS (func_return | TABLE OPEN_PAREN table_func_column_list CLOSE_PAREN)
    // func_return : func_type ; func_type : typename | ... %TYPE.  A `RETURNS SETOF x` carries
    // its SETOF token on the typename itself (typename : SETOF? simpletypename ...).
    private void ParseFunctionReturn(CreateFunctionStatement statement,
        PostgreSQLParser.CreatefunctionstmtContext context)
    {
        // RETURNS TABLE (...) declares result columns rather than a return type. Measured on
        // postgres:18.4: PostgreSQL stores them as ordinary arguments in TABLE mode
        // (proargmodes 't') and sets proretset, so they are appended to the parameter list
        // and the return type is derived exactly as the catalog derives it.
        if (context.TABLE() is not null)
        {
            foreach (var column in context.table_func_column_list().table_func_column())
            {
                statement.Parameters.Add(At(
                    new RoutineParameter(
                        ParseParameterName(column.param_name()),
                        ParameterMode.Table,
                        ParseFuncType(column.func_type(), "RETURNS TABLE column")),
                    column));
            }

            statement.ReturnsSet = true;
            statement.ReturnType = DeriveResultType(statement, ParameterMode.Table);

            return;
        }

        // With no RETURNS clause the OUT parameters define the result, which is how
        // PostgreSQL itself derives prorettype for such a function.
        if (context.RETURNS() is null)
        {
            statement.ReturnType = DeriveResultType(statement, ParameterMode.Out)
                ?? throw new PostgresParseException(
                    "A function without a RETURNS clause must declare at least one OUT parameter");

            return;
        }

        if (context.func_return()?.func_type() is not { } funcType)
        {
            throw new PostgresParseException("Unable to parse function return type");
        }

        var declared = ParseFuncType(funcType, "function return type");

        statement.ReturnType = declared;
        statement.ReturnsSet = funcType.typename()?.SETOF() is not null;
    }

    /// <summary>
    /// Derives the result type of a function whose result comes from its OUT or TABLE
    /// parameters. Measured on postgres:18.4: one such parameter reports prorettype as that
    /// parameter's own type, and two or more report <c>record</c>.
    /// </summary>
    private static DataType? DeriveResultType(CreateFunctionStatement statement, ParameterMode mode)
    {
        var results = statement.Parameters
            .Where(i => i.Mode == mode || i.Mode == ParameterMode.InOut)
            .ToList();

        return results.Count switch
        {
            0 => null,
            1 => results[0].DataType,
            _ => new UnresolvedDataType("record"),
        };
    }

    // func_type : typename | SETOF? type_function_name attrs PERCENT TYPE_P
    // The %TYPE alternative names another object's column rather than a type. PostgreSQL
    // resolves it against the catalog when the routine is created — measured on
    // postgres:18.4, `t.c%TYPE` is stored as plain `integer` — so the declared spelling is
    // not what comes back, and modeling it would re-diff on every deploy.
    private DataType ParseFuncType(PostgreSQLParser.Func_typeContext context, string what)
    {
        if (context.typename() is not { } typename)
        {
            throw new NotImplementedException(
                $"A %TYPE {what} is not supported; PostgreSQL resolves %TYPE against the "
                + "catalog when the routine is created, so the declared form is not what the "
                + "database stores and could not be compared back.");
        }

        if (VisitTypename(typename) is not DataType dataType)
        {
            throw new PostgresParseException($"Unable to parse {what}");
        }

        return dataType;
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
                // The two-string form (AS 'obj_file', 'link_symbol') declares a function
                // implemented in a linked C library. It is parsed so the model builder can
                // report exactly what it is rejecting, rather than failing here as though
                // the declaration were malformed.
                statement.Body = GetRoutineBodyText(funcAs.sconst(0));

                if (funcAs.sconst().Length > 1)
                {
                    statement.LinkSymbol = GetRoutineBodyText(funcAs.sconst(1));
                }

                continue;
            }

            if (option.common_func_opt_item() is { } commonOption)
            {
                ApplyFunctionCommonOption(statement, commonOption);
                continue;
            }

            if (option.WINDOW() is not null)
            {
                statement.IsWindow = true;
                continue;
            }

            if (option.transform_type_list() is { } transforms)
            {
                foreach (var typename in transforms.typename())
                {
                    statement.TransformTypes.Add(
                        VisitTypename(typename) is DataType dataType
                            ? dataType.TypeName
                            : typename.GetText());
                }
            }
        }
    }

    // Unlike a procedure, a function's volatility (IMMUTABLE/STABLE/VOLATILE) and strictness
    // (STRICT / RETURNS NULL ON NULL INPUT vs CALLED ON NULL INPUT) are meaningful and modeled.
    private void ApplyFunctionCommonOption(CreateFunctionStatement statement,
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

        // LEAKPROOF, COST, ROWS, SUPPORT and PARALLEL do not change a function's result, but
        // they do change how the planner may use it, so they are captured here and reported
        // by the model builder rather than dropped in silence (issue #213).
        if (context.LEAKPROOF() is not null)
        {
            statement.Leakproof = context.NOT() is null;
            return;
        }

        if (context.COST() is not null)
        {
            statement.Cost = context.numericonly().GetText();
            return;
        }

        if (context.ROWS() is not null)
        {
            statement.Rows = context.numericonly().GetText();
            return;
        }

        if (context.SUPPORT() is not null)
        {
            statement.SupportFunction = ParseAnyName(context.any_name()).Segments[^1].Name;
            return;
        }

        // PARALLEL takes a colid rather than a keyword, so the level arrives as an
        // identifier; fold it as PostgreSQL folds an unquoted one.
        if (context.PARALLEL() is not null)
        {
            statement.Parallel = context.colid().GetText().ToUpperInvariant();
            return;
        }

        if (context.functionsetresetclause() is { } setReset)
        {
            statement.Settings.Add(ParseRoutineSetting(setReset));
        }
    }

    // functionsetresetclause : SET set_rest_more | variableresetstmt
    //
    // Only the generic `name = value` and `name FROM CURRENT` forms of set_rest_more can
    // appear on a routine; the rest (TIME ZONE, ROLE, SESSION AUTHORIZATION, …) are session
    // statements the grammar happens to share, and PostgreSQL rejects them here.
    private RoutineSetting ParseRoutineSetting(
        PostgreSQLParser.FunctionsetresetclauseContext context)
    {
        if (context.variableresetstmt() is { } reset)
        {
            var resetRest = reset.reset_rest();

            if (resetRest.generic_reset() is not { } genericReset)
            {
                throw new NotImplementedException(
                    $"RESET {resetRest.GetText()} is not valid on a routine declaration");
            }

            // RESET ALL names no parameter, so the setting carries only the ALL marker.
            return At(
                new RoutineSetting(genericReset.ALL() is not null
                    ? "ALL"
                    : ParseVariableName(genericReset.var_name()))
                {
                    IsReset = true,
                    IsAll = genericReset.ALL() is not null,
                },
                context);
        }

        var setRest = context.set_rest_more();

        if (setRest.FROM() is not null && setRest.CURRENT_P() is not null)
        {
            return At(
                new RoutineSetting(ParseVariableName(setRest.var_name())) { FromCurrent = true },
                context);
        }

        if (setRest.generic_set() is not { } genericSet)
        {
            throw new NotImplementedException(
                $"SET {setRest.GetText()} is not valid on a routine declaration");
        }

        var setting = At(new RoutineSetting(ParseVariableName(genericSet.var_name())), context);

        // `SET x = DEFAULT` resets the parameter to the server default, which is what an
        // absent clause already does, so it carries no values and reads as a RESET.
        if (genericSet.var_list() is not { } values)
        {
            setting.IsReset = true;

            return setting;
        }

        foreach (var value in values.var_value())
        {
            setting.Values.Add(ParseVariableValue(value));
        }

        return setting;
    }

    // var_name : colid (DOT colid)*  — a namespaced GUC such as `plpgsql.check_asserts`.
    private static string ParseVariableName(PostgreSQLParser.Var_nameContext context)
        => string.Join('.', context.colid().Select(i => i.GetText().ToLowerInvariant()));

    // var_value : boolean_or_string_ | numericonly.  A quoted value and the same value
    // written bare are the same setting — measured on postgres:18.4, `SET work_mem = '64MB'`
    // and `SET work_mem TO 64MB` both store `work_mem=64MB` — so the value is unwrapped to
    // its text either way.
    private string ParseVariableValue(PostgreSQLParser.Var_valueContext context)
    {
        if (context.boolean_or_string_()?.nonreservedword_or_sconst() is { } wordOrString)
        {
            return GetNonReservedWordOrSconstText(wordOrString);
        }

        return context.GetText();
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
            throw new PostgresParseException("Unable to parse routine parameter type");
        }

        var dataType = ParseFuncType(funcType, "routine parameter");

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
                // Read the body verbatim: this is exactly the text PostgreSQL stores in
                // pg_proc.prosrc, so no canonicalization is needed for the parsed and
                // extracted models to agree.
                //
                // The two-string form (AS 'obj_file', 'link_symbol') declares a procedure
                // implemented in a linked C library; it is parsed so the model builder can
                // name what it is rejecting rather than failing as though it were malformed.
                statement.Body = GetRoutineBodyText(funcAs.sconst(0));

                if (funcAs.sconst().Length > 1)
                {
                    statement.LinkSymbol = GetRoutineBodyText(funcAs.sconst(1));
                }

                continue;
            }

            if (option.common_func_opt_item() is { } commonOption)
            {
                ApplyCommonOption(statement, commonOption);
                continue;
            }

            // WINDOW is accepted by the shared grammar rule but PostgreSQL rejects it on a
            // procedure, so a declaration carrying it is invalid rather than unmodeled.
            if (option.WINDOW() is not null)
            {
                throw new NotImplementedException("WINDOW is not valid on a procedure");
            }

            if (option.transform_type_list() is { } transforms)
            {
                foreach (var typename in transforms.typename())
                {
                    statement.TransformTypes.Add(
                        VisitTypename(typename) is DataType dataType
                            ? dataType.TypeName
                            : typename.GetText());
                }
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

    private void ApplyCommonOption(
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
        // not modeled. A SET clause does affect execution and is modeled (issue #213).
        if (context.functionsetresetclause() is { } setReset)
        {
            statement.Settings.Add(ParseRoutineSetting(setReset));
        }
    }
}
