using Squill.PostgresParser.Syntax;

// ReSharper disable StringLiteralTypo
// ReSharper disable CommentTypo
// ReSharper disable IdentifierTypo

namespace Squill.PostgresParser;

public class PostgresVisitor : PostgreSQLParserBaseVisitor<SyntaxNode?>
{
    public override SyntaxNode VisitRoot(PostgreSQLParser.RootContext context)
    {
        var root = new Root();
        
        foreach (var stmtContext in context.stmtblock().stmtmulti().stmt())
        {
            var stmt = VisitStmt(stmtContext);

            if (stmt is not Statement statement)
            {
                throw new PostgresParseException("Expected VisitStmt to return a Statement");
            }
            
            root.Statements.Add(statement);
        }

        return root;
    }

    public override SyntaxNode? VisitCreatestmt(PostgreSQLParser.CreatestmtContext context)
    {
        // TODO: support opttemp
        // TODO: support if not exists
        // TODO: support OF and PARTITION OF

        if (context.qualified_name().Length == 0)
        {
            throw new PostgresParseException("Expected CREATE TABLE statement to have a qualified name");
        }

        if (VisitQualified_name(context.qualified_name()[0]) is not QualifiedName qualifiedName)
        {
            throw new PostgresParseException("Unable to parse qualified name for CREATE TABLE statement");
        }

        if (context.opttableelementlist() is not { } opttableelementlist)
        {
            throw new NotImplementedException("OF and PARTITION OF not yet supported for CREATE TABLE statements");
        }

        var createTable = new CreateTableStatement(qualifiedName);
        
        if (opttableelementlist.tableelementlist() is { } tableelementlist)
        {
            foreach (var tableelementContext in tableelementlist.tableelement())
            {
                var tableElementNode = VisitTableelement(tableelementContext);

                if (tableElementNode is not ITableElement tableElement)
                {
                    throw new PostgresParseException("Unable to parse table element");
                }
                
                createTable.Elements.Add(tableElement);
            }
        }

        return createTable;
    }

    public override SyntaxNode VisitQualified_name(PostgreSQLParser.Qualified_nameContext context)
    {
        if (context.colid().identifier()?.Identifier() is not { } first)
        {
            throw new NotImplementedException("Only basic unquoted identifiers are not yet supported");
        }

        var segments = new List<string>
        {
            first.GetText()
        };

        if (context.indirection() is not null)
        {
            throw new NotImplementedException("Dotted qualified names are not yet supported");
        }

        return new QualifiedName(segments);
    }

    public override SyntaxNode VisitTableelement(PostgreSQLParser.TableelementContext context)
    {
        if (context.columnDef() is not { } columnDefContext)
        {
            throw new NotImplementedException("Table constraints and LIKE clauses are not yet supported");
        }

        string name;
        
        if (columnDefContext.colid().identifier() is { } identifier)
        {
            if (identifier.Identifier() is { } identifierName)
            {
                name = identifierName.GetText();
            }
            else
            {
                throw new NotImplementedException("Support for quoted identifiers and other identifier types not yet implemented");
            }
        }
        else if (columnDefContext.colid().unreserved_keyword() is { } unreservedKeyword)
        {
            name = unreservedKeyword.GetText();
        }
        else if (columnDefContext.colid().plsql_unreserved_keyword() is { } plsqlUnreservedKeyword)
        {
            name = plsqlUnreservedKeyword.GetText();
        }
        else if (columnDefContext.colid().col_name_keyword() is { } colNameKeyword)
        {
            name = colNameKeyword.GetText();
        }
        else
        {
            throw new NotImplementedException("Column identifier type not implemented");
        }

        if (VisitTypename(columnDefContext.typename()) is not DataType dataType)
        {
            throw new PostgresParseException("Unable to parse table element data type");
        }
        
        // TODO: support OPTIONS
        
        var columnDef = new ColumnDefinition(name, dataType);

        if (columnDefContext.colquallist() is { } colquallist
            && colquallist.colconstraint() is { Length: > 0 } colconstraints)
        {
            foreach (var colconstraint in colconstraints)
            {
                if (colconstraint.CONSTRAINT() is not null
                    && colconstraint.name() is { } nameContext)
                {
                    if (VisitColconstraintelem(colconstraint.colconstraintelem()) is not ColumnConstraint innerConstraint)
                    {
                        throw new PostgresParseException("Expected VisitColconstraintelem to return a ColumnConstraint");
                    }
                    
                    // TODO: support quoted identifiers properly etc. instead of just calling nameContext.GetText()
                    columnDef.Constraints.Add(new NamedColumnConstraint(colconstraint.GetText(), nameContext.GetText(), innerConstraint));
                }
                else if (colconstraint.colconstraintelem() is { } colconstraintelem)
                {
                    if (VisitColconstraintelem(colconstraintelem) is not ColumnConstraint columnConstraint)
                    {
                        throw new PostgresParseException("Expected VisitColconstraintelem to return a ColumnConstraint");
                    }
                    
                    columnDef.Constraints.Add(columnConstraint);
                }
                else
                {
                    // TODO: support these constraint types
                    throw new NotImplementedException("DEFERRABLE, DEFERRED, IMMEDIATE, and COLLATE not yet supported");
                }
            }   
        }

        return columnDef;
    }

    public override SyntaxNode VisitTypename(PostgreSQLParser.TypenameContext context)
    {
        if (context.simpletypename() is not { } simpletypenameContext)
        {
            throw new NotImplementedException("PERCENT not yet supported");
        }

        if (simpletypenameContext.character() is { } characterContext
            && characterContext.character_c() is { } character_c)
        {
            var length = characterContext.iconst() is { } iconst ? int.Parse(iconst.Integral().GetText()) : (int?)null;

            if (character_c.VARCHAR() is not null
                || (character_c.CHARACTER() is not null
                    && character_c.opt_varying()?.VARYING() is not null))
            {
                var type = new BuiltInDataType(PostgresBuiltInDataType.Varchar, character_c.GetText());

                if (length != null)
                {
                    type.Modifiers.Add(length.Value);
                }

                return type;
            }

            if (character_c.CHARACTER() is not null
                || character_c.CHAR_P() is not null
                || character_c.NCHAR() is not null)
            {
                // TODO: is this handling of nchar correct? It's not documented.
                var type = new BuiltInDataType(PostgresBuiltInDataType.Char, character_c.GetText());

                if (length != null)
                {
                    type.Modifiers.Add(length.Value);
                }

                return type;
            }

            throw new PostgresParseException($"Unknown or unsupported character type: {character_c.GetText()}");
        }

        if (simpletypenameContext.numeric() is { } numeric)
        {
            if (numeric.INT_P() is not null || numeric.INTEGER() is not null)
            {
                return new BuiltInDataType(PostgresBuiltInDataType.Integer, numeric.GetText());
            }

            if (numeric.SMALLINT() is not null)
            {
                return new BuiltInDataType(PostgresBuiltInDataType.SmallInt, numeric.GetText());
            }

            if (numeric.BIGINT() is not null)
            {
                return new BuiltInDataType(PostgresBuiltInDataType.BigInt, numeric.GetText());
            }

            if (numeric.REAL() is not null)
            {
                return new BuiltInDataType(PostgresBuiltInDataType.Real, numeric.GetText());
            }

            if (numeric.DOUBLE_P() is not null)
            {
                return new BuiltInDataType(PostgresBuiltInDataType.Double, numeric.GetText());
            }
            
            // TODO: support decimal/numeric, serial types, boolean
        }

        if (simpletypenameContext.constdatetime() is { } constdatetime)
        {
            bool withTimeZone = constdatetime.opt_timezone()?.WITH() != null;

            if (constdatetime.iconst() is not null)
            {
                throw new NotImplementedException("Support for modifiers on TIME and TIMESTAMP types not yet implemented");
            }

            if (constdatetime.TIME() is not null)
            {
                return new BuiltInDataType(
                    withTimeZone ? PostgresBuiltInDataType.TimeWithTimeZone : PostgresBuiltInDataType.Time,
                    constdatetime.GetText());
            }

            if (constdatetime.TIMESTAMP() is not null)
            {
                return new BuiltInDataType(
                    withTimeZone ? PostgresBuiltInDataType.TimestampWithTimeZone : PostgresBuiltInDataType.Timestamp,
                    constdatetime.GetText());
            }
        }

        if (simpletypenameContext.generictype() is { } generictype)
        {
            if (generictype.type_function_name() is { } typeFunctionName)
            {
                var text = typeFunctionName.GetText();

                if (Enum.TryParse<PostgresObjectIdentifierTypes>(text, ignoreCase: true, out var oidType))
                {
                    return new ObjectIdentifierTypeName(text, oidType);
                }
            }
        }
    
        throw new NotImplementedException($"Support for {simpletypenameContext.GetText()} type name not yet implemented");
    }

    public override SyntaxNode VisitColconstraintelem(PostgreSQLParser.ColconstraintelemContext context)
    {
        if (context.NULL_P() is not null)
        {
            return new NullableColumnConstraint(context.GetText(), context.NOT() is null);
        }

        if (context.PRIMARY() is not null && context.KEY() is not null)
        {
            // TODO: support opt_definition and optconsttablespace
            return new PrimaryKeyColumnConstraint(context.GetText());
        }

        if (context.DEFAULT() is not null)
        {
            if (VisitB_expr(context.b_expr()) is not Expression expression)
            {
                throw new PostgresParseException("Expected an Expression for DEFAULT constraint");
            }

            return new DefaultColumnConstraint(context.GetText(), expression);
        }

        // TODO: support UNIQUE, CHECK, DEFAULT, GENERATED, and REFERENCES
        throw new NotImplementedException("Column constraint type not yet implemented");
    }

    public override SyntaxNode? VisitB_expr(PostgreSQLParser.B_exprContext context)
    {
        if (context.c_expr() is { } cExpr)
        {
            return Visit(cExpr);
        }

        throw new NotImplementedException("b_expr expression alternate not yet supported");
    }

    public override SyntaxNode VisitC_expr_expr(PostgreSQLParser.C_expr_exprContext context)
    {
        if (context.func_expr() is { } funcExpr)
        {
            return VisitFunc_expr(funcExpr);
        }

        if (context.aexprconst() is { } aexprconst)
        {
            return VisitAexprconst(aexprconst);
        }

        throw new NotImplementedException("c_expr_expr expression alternate not yet supported");
    }

    public override SyntaxNode VisitAexprconst(PostgreSQLParser.AexprconstContext context)
    {
        if (context.sconst() is { } sconst)
        {
            return VisitSconst(sconst);
        }

        throw new NotImplementedException("Aexprconst alternate not yet supported");
    }

    public override SyntaxNode VisitFunc_expr(PostgreSQLParser.Func_exprContext context)
    {
        if (context.func_application() is { } funcApplication)
        {
            return VisitFunc_application(funcApplication);
        }

        throw new NotImplementedException("Support for func_expr_common_subexpr not yet implemented");
    }

    public override SyntaxNode VisitFunc_application(PostgreSQLParser.Func_applicationContext context)
    {
        // TODO: handle names better, i.e. quoted identifiers
        var name = context.func_name().GetText();

        var func = new FunctionApplicationExpression(name);

        if (context.func_arg_expr() is not null
            || context.VARIADIC() is not null
            || context.ALL() is not null
            || context.DISTINCT() is not null
            || context.STAR() is not null)
        {
            throw new NotImplementedException("Support for variadic arguments, VARIADIC, ALL, DISTINCT, and * not yet implemented");
        }
        
        if (context.func_arg_list() is { } funcArgList)
        {
            foreach (var argExpr in funcArgList.func_arg_expr())
            {
                if (argExpr.param_name() is not null)
                {
                    throw new NotImplementedException("Support for named parameters is not yet implemented");
                }

                if (VisitA_expr(argExpr.a_expr()) is not Expression expression)
                {
                    throw new PostgresParseException("Expected argument a_expr to return an Expression");
                }

                func.Arguments.Add(new FunctionArgument(expression));
            }
        }
        
        return func;
    }

    public override SyntaxNode VisitA_expr_typecast(PostgreSQLParser.A_expr_typecastContext context)
    {
        TypecastExpression? expression = null;

        foreach (var typename in context.typename())
        {
            if (VisitTypename(typename) is not DataType dataType)
            {
                throw new PostgresParseException("Unable to parse data type for typecast");
            }

            if (expression != null)
            {
                expression = new TypecastExpression(expression, dataType);
            }
            else
            {
                var startExprNode = Visit(context.c_expr());
                
                if (startExprNode is not Expression startExpression)
                {
                    throw new PostgresParseException("Unable to parse expression for typecast");
                }

                expression = new TypecastExpression(startExpression, dataType);
            }
        }

        if (expression == null)
        {
            throw new PostgresParseException("Unexpected missing typename in typecast expression");
        }        
        
        return expression;
    }

    public override SyntaxNode VisitSconst(PostgreSQLParser.SconstContext context)
    {
        if (context.opt_uescape() is not null && context.opt_uescape().UESCAPE() is not null)
        {
            throw new NotImplementedException("UESCAPE not yet supported");
        }

        return VisitAnysconst(context.anysconst());
    }

    public override SyntaxNode VisitAnysconst(PostgreSQLParser.AnysconstContext context)
    {
        if (context.StringConstant() is not null)
        {
            var text = context.GetText();

            if (text[0] != '\'' || text[^1] != '\'')
            {
                throw new PostgresParseException("Expected string literal to start and end with \"'\"");
            }

            var stringValue = text[1..^1].Replace("''", "'");

            return new LiteralExpression(text, stringValue);
        }

        throw new NotImplementedException("Support for other string constant types not yet implemented");
    }
}