using Antlr4.Runtime.Tree;
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

    public override SyntaxNode VisitCreatestmt(PostgreSQLParser.CreatestmtContext context)
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
        string first;
        
        if (context.colid().identifier()?.Identifier() is { } identifier)
        {
            first = identifier.GetText();
        }
        else if (context.colid().unreserved_keyword() is { } unreservedKeyword)
        {
            first = unreservedKeyword.GetText();
        }
        else
        {
            throw new PostgresParseException("Unsupported quoted identifier");
        }

        var segments = new List<string>
        {
            first
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

        var arraySizes = new Stack<int?>();
        DataType? dataType = null;

        if (context.opt_array_bounds()?.OPEN_BRACKET() is { Length: > 0 })
        {
            int? size = null;
            
            // HACK.PI: is there a better way to do this?
            // opt_array_bounds().iconst() is not the same size array as OPEN_BRACKET() so we can't tell from arrays
            // alone which size goes with which dimension
            foreach (var arrayBoundChild in context.opt_array_bounds().children)
            {
                if (arrayBoundChild is PostgreSQLParser.IconstContext arrayBound)
                {
                    if (VisitIconst(arrayBound) is not LiteralExpression { Value: long arraySize })
                    {
                        throw new PostgresParseException("Unable to parse array bound literal");
                    }
                    
                    size = Convert.ToInt32(arraySize);
                }
                else if (arrayBoundChild is TerminalNodeImpl terminalNode &&
                    terminalNode.Symbol.Type == PostgreSQLLexer.CLOSE_BRACKET)
                {
                    arraySizes.Push(size);
                    size = null;
                }
            }
        }

        if (simpletypenameContext.character() is { } characterContext
            && characterContext.character_c() is { } character_c)
        {
            var length = characterContext.iconst() is { } iconst 
                ? VisitIconst(iconst) as Expression ?? throw new PostgresParseException("Unable to parse character length expression") 
                : null;

            if (character_c.VARCHAR() is not null
                || (character_c.CHARACTER() is not null
                    && character_c.opt_varying()?.VARYING() is not null))
            {
                var type = new BuiltInDataType(PostgresBuiltInDataType.Varchar, character_c.GetText());

                if (length != null)
                {
                    type.Modifiers.Add(length);
                }

                dataType = type;
            }
            else if (character_c.CHARACTER() is not null
                || character_c.CHAR_P() is not null
                || character_c.NCHAR() is not null)
            {
                // TODO: is this handling of nchar correct? It's not documented.
                var type = new BuiltInDataType(PostgresBuiltInDataType.Char, character_c.GetText());

                if (length != null)
                {
                    type.Modifiers.Add(length);
                }

                dataType = type;
            }
            else
            {
                throw new PostgresParseException($"Unknown or unsupported character type: {character_c.GetText()}");
            }
        }

        if (simpletypenameContext.numeric() is { } numeric)
        {
            if (numeric.INT_P() is not null || numeric.INTEGER() is not null)
            {
                dataType = new BuiltInDataType(PostgresBuiltInDataType.Integer, numeric.GetText());
            }
            else if (numeric.SMALLINT() is not null)
            {
                dataType = new BuiltInDataType(PostgresBuiltInDataType.SmallInt, numeric.GetText());
            }
            else if (numeric.BIGINT() is not null)
            {
                dataType = new BuiltInDataType(PostgresBuiltInDataType.BigInt, numeric.GetText());
            }
            else if (numeric.REAL() is not null)
            {
                dataType = new BuiltInDataType(PostgresBuiltInDataType.Real, numeric.GetText());
            }
            else if (numeric.DOUBLE_P() is not null)
            {
                dataType = new BuiltInDataType(PostgresBuiltInDataType.Double, numeric.GetText());
            }
            else if (numeric.BOOLEAN_P() is not null)
            {
                dataType = new BuiltInDataType(PostgresBuiltInDataType.Boolean, numeric.GetText());
            }
            else if (numeric.NUMERIC() is not null || numeric.DECIMAL_P() is not null || numeric.DEC() is not null)
            {
                var numericType = new BuiltInDataType(PostgresBuiltInDataType.Decimal, numeric.GetText());

                if (numeric.opt_type_modifiers()?.expr_list() is { } numericModifiers)
                {
                    foreach (var numericModifierExpr in numericModifiers.a_expr())
                    {
                        if (VisitA_expr(numericModifierExpr) is not Expression expression)
                        {
                            throw new PostgresParseException("Unable to parse numeric type modifier expression");
                        }
                        
                        numericType.Modifiers.Add(expression);
                    }
                }

                dataType = numericType;
            }
            else
            {
                // TODO: support serial types
                throw new NotImplementedException("Specified numeric type not yet supported");
            }
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
                dataType = new BuiltInDataType(
                    withTimeZone ? PostgresBuiltInDataType.TimeWithTimeZone : PostgresBuiltInDataType.Time,
                    constdatetime.GetText());
            }
            else if (constdatetime.TIMESTAMP() is not null)
            {
                dataType = new BuiltInDataType(
                    withTimeZone ? PostgresBuiltInDataType.TimestampWithTimeZone : PostgresBuiltInDataType.Timestamp,
                    constdatetime.GetText());
            }
        }

        if (simpletypenameContext.generictype() is { } generictype)
        {
            if (generictype.attrs() is not null)
            {
                throw new NotImplementedException("Attributes on generic types are not yet supported");
            }

            if (generictype.opt_type_modifiers()?.expr_list() is not null)
            {
                throw new NotImplementedException("Type modifiers are not yet supported");
            }
            
            if (generictype.type_function_name() is { } typeFunctionName)
            {
                var text = typeFunctionName.GetText();

                if (typeFunctionName.unreserved_keyword() is { } unreservedKeyword)
                {
                    if (unreservedKeyword.TEXT_P() is not null)
                    {
                        dataType = new BuiltInDataType(PostgresBuiltInDataType.Text, text);
                    }
                    else
                    {
                        dataType = new UnresolvedDataType(text);
                    }
                }
                else if (Enum.TryParse<PostgresObjectIdentifierTypes>(text, ignoreCase: true, out var oidType))
                {
                    dataType = new ObjectIdentifierTypeName(text, oidType);
                }
                else if (Enum.TryParse<PostgresBuiltInDataType>(text, ignoreCase: true, out var builtInUnparsedType)
                         && builtInUnparsedType is PostgresBuiltInDataType.TSVector 
                             or PostgresBuiltInDataType.TSQuery
                             or PostgresBuiltInDataType.Date)
                {
                    // TODO: modify parser/lexer to support these types and PR upstream
                    dataType = new BuiltInDataType(builtInUnparsedType, text);
                }
                else
                {
                    dataType = new UnresolvedDataType(text);
                }
            }
        }

        if (dataType == null)
        {
            throw new NotImplementedException(
                $"Support for {simpletypenameContext.GetText()} type name not yet implemented");
        }

        if (arraySizes.Count == 0)
        {
            return dataType;
        }

        while (arraySizes.TryPop(out var size))
        {
            // TODO: multi-dimensional arrays will have incorrect text, probably
            dataType = new ArrayDataType(context.GetText(), dataType, size);
        }

        return dataType;
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

        if (context.TYPECAST() is not null)
        {
            if (VisitB_expr(context.b_expr()[0]) is not Expression expression)
            {
                throw new PostgresParseException("Unable to parse typecast expression");
            }

            if (VisitTypename(context.typename()) is not DataType dataType)
            {
                throw new PostgresParseException("Unable to parse typecast typename");
            }

            return new TypecastExpression(expression, dataType);
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

        if (context.a_expr() is { } aExpr)
        {
            if (context.opt_indirection()?.ChildCount is > 0)
            {
                throw new NotImplementedException("Indirection after parenthesized expressions not yet supported");
            }

            if (VisitA_expr(aExpr) is not Expression expression)
            {
                throw new PostgresParseException("Unable to parse parenthesized expression");
            }

            return new ParenthesizedExpression(expression);
        }

        throw new NotImplementedException("c_expr_expr expression alternate not yet supported");
    }

    public override SyntaxNode VisitAexprconst(PostgreSQLParser.AexprconstContext context)
    {
        if (context.sconst() is { } sconst)
        {
            return VisitSconst(sconst);
        }

        if (context.iconst() is { } iconst)
        {
            return VisitIconst(iconst);
        }

        if (context.fconst() is { } fconst)
        {
            return VisitFconst(fconst);
        }

        if (context.TRUE_P() is not null)
        {
            return new LiteralExpression(context.GetText(), true);
        }

        if (context.FALSE_P() is not null)
        {
            return new LiteralExpression(context.GetText(), false);
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

    public override SyntaxNode? VisitA_expr_typecast(PostgreSQLParser.A_expr_typecastContext context)
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

        // the way these grammar rules are set up, this passes through to c_expr if there is no typename
        return expression ?? Visit(context.c_expr());
    }

    public override SyntaxNode VisitSconst(PostgreSQLParser.SconstContext context)
    {
        if (context.opt_uescape() is not null && context.opt_uescape().UESCAPE() is not null)
        {
            throw new NotImplementedException("UESCAPE not yet supported");
        }

        return VisitAnysconst(context.anysconst());
    }

    public override SyntaxNode VisitIconst(PostgreSQLParser.IconstContext context)
    {
        return new LiteralExpression(context.GetText(), long.Parse(context.GetText()));
    }
    
    public override SyntaxNode VisitFconst(PostgreSQLParser.FconstContext context)
    {
        return new LiteralExpression(context.GetText(), decimal.Parse(context.GetText()));
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