using Antlr4.Runtime.Tree;
using Squill.PostgresParser.Syntax;
using Expression = Squill.PostgresParser.Syntax.Expression;

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

        if (context.optinherit()?.INHERITS() is not null)
        {
            foreach (var inheritQualName in context.optinherit().qualified_name_list().qualified_name())
            {
                if (VisitQualified_name(inheritQualName) is not QualifiedName inherits)
                {
                    throw new PostgresParseException("Unable to parse table INHERITS clause");
                }
                
                createTable.Inherits.Add(inherits);
            }
        }

        return createTable;
    }

    public override SyntaxNode VisitQualified_name(PostgreSQLParser.Qualified_nameContext context)
    {
        if (VisitColid(context.colid()) is not Identifier first)
        {
            throw new PostgresParseException("Unable to parse qualified name identifier");
        }

        var segments = new List<Identifier>
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
        if (context.columnDef() is { } columnDefContext)
        {
            return VisitColumnDef(columnDefContext);
        }

        if (context.tableconstraint() is { } tableconstraint)
        {
            return VisitTableconstraint(tableconstraint);
        }

        throw new NotImplementedException("Table LIKE elements not yet supported");
    }

    public override SyntaxNode VisitTableconstraint(PostgreSQLParser.TableconstraintContext context)
    {
        if (VisitConstraintelem(context.constraintelem()) is not TableConstraint constraint)
        {
            throw new PostgresParseException("Unable to parse table constraint element");
        }
        
        if (context.CONSTRAINT() is not null
            && context.name() is { } nameContext)
        {
            if (VisitColid(nameContext.colid()) is not Identifier nameIdentifier)
            {
                throw new PostgresParseException("Unable to parse named constraint identifier");
            }

            return new NamedTableConstraint(nameIdentifier, constraint);
        }

        return constraint;
    }

    public override SyntaxNode VisitConstraintelem(PostgreSQLParser.ConstraintelemContext context)
    {
        if (context.CHECK() is not null)
        {
            if (VisitA_expr(context.a_expr()) is not Expression checkExpression)
            {
                throw new PostgresParseException("Unable to parse CHECK constraint expression");
            }

            return new CheckTableConstraint(checkExpression);
        }

        throw new NotImplementedException("Table constraint type not yet implemented");
    }

    public override SyntaxNode VisitColumnDef(PostgreSQLParser.ColumnDefContext context)
    {
        if (VisitColid(context.colid()) is not Identifier name)
        {
            throw new PostgresParseException("Unable to parse column name");
        }

        if (VisitTypename(context.typename()) is not DataType dataType)
        {
            throw new PostgresParseException("Unable to parse table element data type");
        }
        
        // TODO: support OPTIONS
        
        var columnDef = new ColumnDefinition(name, dataType);

        if (context.colquallist() is { } colquallist
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

    public override SyntaxNode VisitUnreserved_keyword(PostgreSQLParser.Unreserved_keywordContext context)
    {
        // TODO: do unreserved keywords deserve their own type?
        return new SimpleIdentifier(context.GetText());
    }

    public override SyntaxNode VisitPlsql_unreserved_keyword(PostgreSQLParser.Plsql_unreserved_keywordContext context)
    {
        // TODO: do PLSQL unreserved keywords deserve their own type?
        return new SimpleIdentifier(context.GetText());
    }

    public override SyntaxNode VisitCol_name_keyword(PostgreSQLParser.Col_name_keywordContext context)
    {
        // TODO: do col name keywords deserve their own type?
        return new SimpleIdentifier(context.GetText());
    }

    public override SyntaxNode VisitIdentifier(PostgreSQLParser.IdentifierContext context)
    {
        if (context.Identifier() is { } identifierName)
        {
            if (context.opt_uescape()?.UESCAPE() is not null)
            {
                throw new NotImplementedException("Support for UESCAPE not yet implemented");
            }
            
            return new SimpleIdentifier(identifierName.GetText());
        }

        var unicodeQuoted = context.UnicodeQuotedIdentifier();

        if (context.QuotedIdentifier() is not null
            || unicodeQuoted is not null)
        {
            string text = context.GetText();

            if (text.StartsWith("U&"))
            {
                text = text[2..];
            }

            if (text[0] != '"' || text[^1] != '"')
            {
                throw new NotImplementedException("Unable to parse quoted identifier");
            }

            string name = text[1..^1];

            return new SimpleIdentifier(name, isQuoted: true, isUnicodeQuoted: unicodeQuoted is not null);
        }

        if (context.plsqlvariablename() is { } plsqlvariablename)
        {
            return VisitPlsqlvariablename(plsqlvariablename);
        }

        if (context.plsql_unreserved_keyword() is { } plsqlUnreservedKeyword)
        {
            return VisitPlsql_unreserved_keyword(plsqlUnreservedKeyword);
        }

        throw new NotImplementedException("Support for quoted identifiers and other identifier types not yet implemented");
    }

    public override SyntaxNode VisitPlsqlvariablename(PostgreSQLParser.PlsqlvariablenameContext context)
    {
        var name = context.PLSQLVARIABLENAME().GetText().TrimStart(':');
        return new PLSQLVariableName(name);
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
                else if (text.Equals("bytea", StringComparison.OrdinalIgnoreCase))
                {
                    // TODO: modify parser/lexer to support bytea and PR upstream
                    dataType = new BuiltInDataType(PostgresBuiltInDataType.ByteArray, text);
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

        if (context.b_expr() is { Length: 2 } binaryExpression)
        {
            if (VisitB_expr(binaryExpression[0]) is not Expression left)
            {
                throw new PostgresParseException("Unable to parse left side of binary expression");
            }
            
            if (VisitB_expr(binaryExpression[1]) is not Expression right)
            {
                throw new PostgresParseException("Unable to parse right side of binary expression");
            }

            PostgresBuiltInBinaryOperator builtInOperator;
            
            if (context.CARET() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Exponentiation;
            }
            else if (context.STAR() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Multiplication;
            }
            else if (context.SLASH() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Division;
            }
            else if (context.PERCENT() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Modulo;
            }
            else if (context.PLUS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Addition;
            }
            else if (context.MINUS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Subtraction;
            }
            else if (context.LT() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.LessThan;
            }
            else if (context.LESS_EQUALS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.LessThanEqual;
            }
            else if (context.GT() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.GreaterThan;
            }
            else if (context.GREATER_EQUALS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.GreaterThanEqual;
            }
            else if (context.EQUAL() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.Equal;
            }
            else if (context.NOT_EQUALS() is not null)
            {
                builtInOperator = PostgresBuiltInBinaryOperator.NotEqual;
            }
            else
            {
                throw new NotImplementedException("Other operator types not yet supported");
            }

            return new BinaryExpression(left, new BuiltInOperator(builtInOperator), right);
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

        if (context.columnref() is { } columnref)
        {
            return VisitColumnref(columnref);
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

    public override SyntaxNode VisitColumnref(PostgreSQLParser.ColumnrefContext context)
    {
        if (VisitColid(context.colid()) is not Identifier identifier)
        {
            throw new PostgresParseException("Unable to parse column reference identifier");
        }

        return new ColumnReferenceExpression(identifier);
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

    public override SyntaxNode VisitA_expr_typecast(PostgreSQLParser.A_expr_typecastContext context)
    {
        if (context.TYPECAST() is null or { Length: 0 })
        {
            return Visit(context.c_expr()) ?? throw new PostgresParseException("Unable to parse expression");
        }
        
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
            throw new PostgresParseException("Unable to parse typecast expression");
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

    public override SyntaxNode VisitA_expr_or(PostgreSQLParser.A_expr_orContext context)
    {
        if (context.OR() is null or { Length: 0 })
        {
            return VisitA_expr_and(context.a_expr_and()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_andContext>(
            context.children,
            VisitA_expr_and,
            op => op switch
            {
                PostgreSQLLexer.OR => PostgresBuiltInBinaryOperator.Or,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }

    public override SyntaxNode VisitA_expr_and(PostgreSQLParser.A_expr_andContext context)
    {
        if (context.AND() is null or { Length: 0 })
        {
            return VisitA_expr_in(context.a_expr_in()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_inContext>(
            context.children,
            VisitA_expr_in,
            op => op switch
            {
                PostgreSQLLexer.AND => PostgresBuiltInBinaryOperator.And,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }

    public override SyntaxNode VisitA_expr_in(PostgreSQLParser.A_expr_inContext context)
    {
        if (context.IN_P() is null)
        {
            return VisitA_expr_unary_not(context.a_expr_unary_not());
        }

        bool not = context.NOT() is not null;

        if (VisitA_expr_unary_not(context.a_expr_unary_not()) is not Expression left)
        {
            throw new PostgresParseException("Unable to parse IN expression left operand");
        }

        // NOTE: using base Visit method because of named in_expr branches
        // TODO: should we assert this is a more specific type i.e. InExpression?
        if (Visit(context.in_expr()) is not Expression right)
        {
            throw new PostgresParseException("Unable to parse IN expression right operand");
        }

        return new BinaryExpression(
            left,
            new BuiltInOperator(not ? PostgresBuiltInBinaryOperator.NotIn : PostgresBuiltInBinaryOperator.In),
            right
        );
    }

    public override SyntaxNode VisitA_expr_lessless(PostgreSQLParser.A_expr_lesslessContext context)
    {
        if (context.LESS_LESS() is null or { Length: 0 }
            && context.GREATER_GREATER() is null or { Length: 0 })
        {
            return VisitA_expr_or(context.a_expr_or()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_orContext>(
            context.children,
            VisitA_expr_or,
            op => op switch
            {
                PostgreSQLLexer.LESS_LESS => PostgresBuiltInBinaryOperator.LeftShift,
                PostgreSQLLexer.GREATER_GREATER => PostgresBuiltInBinaryOperator.RightShift,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }

    public override SyntaxNode VisitA_expr_unary_not(PostgreSQLParser.A_expr_unary_notContext context)
    {
        if (VisitA_expr_isnull(context.a_expr_isnull()) is not Expression expr)
        {
            throw new PostgresParseException("Unable to parse unary NOT expression");
        }
        
        if (context.NOT() is null)
        {
            return expr;
        }

        return new UnaryExpression(PostgresBuiltInUnaryOperator.Not, expr);
    }

    public override SyntaxNode VisitA_expr_isnull(PostgreSQLParser.A_expr_isnullContext context)
    {
        if (VisitA_expr_is_not(context.a_expr_is_not()) is not Expression expr)
        {
            throw new PostgresParseException("Unable to parse ISNULL/NOTNULL expression");
        }
        
        PostgresBuiltInUnaryOperator op;

        if (context.ISNULL() is not null)
        {
            op = PostgresBuiltInUnaryOperator.IsNull;
        }
        else if (context.NOTNULL() is not null)
        {
            op = PostgresBuiltInUnaryOperator.NotNull;
        }
        else
        {
            return expr;
        }

        return new UnaryExpression(op, expr);
    }

    public override SyntaxNode VisitA_expr_is_not(PostgreSQLParser.A_expr_is_notContext context)
    {
        if (context.IS() is not null)
        {
            throw new NotImplementedException("Support for IS (NOT) not yet implemented");
        }

        return VisitA_expr_compare(context.a_expr_compare());
    }

    public override SyntaxNode VisitA_expr_compare(PostgreSQLParser.A_expr_compareContext context)
    {
        if (context.subquery_Op() is not null)
        {
            throw new NotImplementedException("Subquery_op not yet supported for compare expression");
        }
        
        if (context.a_expr_like() is { Length: 1 })
        {
            return VisitA_expr_like(context.a_expr_like()[0]);
        }

        PostgresBuiltInBinaryOperator op;

        if (context.LT() is not null)
        {
            op = PostgresBuiltInBinaryOperator.LessThan;
        }
        else if (context.GT() is not null)
        {
            op = PostgresBuiltInBinaryOperator.GreaterThan;
        }
        else if (context.EQUAL() is not null)
        {
            op = PostgresBuiltInBinaryOperator.Equal;
        }
        else if (context.LESS_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.LessThanEqual;
        }
        else if (context.GREATER_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.GreaterThanEqual;
        }
        else if (context.NOT_EQUALS() is not null)
        {
            op = PostgresBuiltInBinaryOperator.NotEqual;
        }
        else
        {
            throw new PostgresParseException("Unexpected binary operator in compare expression");
        }

        if (VisitA_expr_like(context.a_expr_like()[0]) is not Expression left
            || VisitA_expr_like(context.a_expr_like()[1]) is not Expression right)
        {
            throw new PostgresParseException("Unable to parse left or right side of compare expression");
        }

        return new BinaryExpression(
            left,
            new BuiltInOperator(op),
            right
        );
    }

    public override SyntaxNode VisitA_expr_like(PostgreSQLParser.A_expr_likeContext context)
    {
        if (context.LIKE() is not null
            || context.ILIKE() is not null
            || context.SIMILAR() is not null
            || context.BETWEEN() is not null)
        {
            throw new NotImplementedException("LIKE/ILIKE/SIMILAR/BETWEEN not yet supported");
        }

        return VisitA_expr_qual_op(context.a_expr_qual_op()[0]);
    }

    public override SyntaxNode VisitA_expr_qual_op(PostgreSQLParser.A_expr_qual_opContext context)
    {
        if (context.qual_op() is { Length: > 0 })
        {
            throw new NotImplementedException("qual_op not yet supported");
        }

        return VisitA_expr_unary_qualop(context.a_expr_unary_qualop()[0]);
    }

    public override SyntaxNode VisitA_expr_unary_qualop(PostgreSQLParser.A_expr_unary_qualopContext context)
    {
        if (context.qual_op() is not null)
        {
            throw new NotImplementedException("qual_op not yet supported");
        }

        return VisitA_expr_add(context.a_expr_add());
    }

    public override SyntaxNode VisitA_expr_add(PostgreSQLParser.A_expr_addContext context)
    {
        if (context.a_expr_mul() is { Length: 1 })
        {
            return VisitA_expr_mul(context.a_expr_mul()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_mulContext>(
            context.children,
            VisitA_expr_mul,
            op => op switch
            {
                PostgreSQLLexer.MINUS => PostgresBuiltInBinaryOperator.Subtraction,
                PostgreSQLLexer.PLUS => PostgresBuiltInBinaryOperator.Addition,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }

    public override SyntaxNode VisitA_expr_mul(PostgreSQLParser.A_expr_mulContext context)
    {
        if (context.a_expr_caret() is { Length: 1 })
        {
            return VisitA_expr_caret(context.a_expr_caret()[0]);
        }

        return VisitBinaryExpression<PostgreSQLParser.A_expr_caretContext>(
            context.children,
            VisitA_expr_caret,
            op => op switch
            {
                PostgreSQLLexer.STAR => PostgresBuiltInBinaryOperator.Multiplication,
                PostgreSQLLexer.SLASH => PostgresBuiltInBinaryOperator.Division,
                PostgreSQLLexer.PERCENT => PostgresBuiltInBinaryOperator.Modulo,
                _ => throw new PostgresParseException("Unexpected operator"),
            }
        );
    }

    public override SyntaxNode VisitA_expr_caret(PostgreSQLParser.A_expr_caretContext context)
    {
        if (context.CARET() is not null)
        {
            throw new NotImplementedException("Caret operator is not yet implemented");
        }

        return VisitA_expr_unary_sign(context.a_expr_unary_sign());
    }

    public override SyntaxNode VisitA_expr_unary_sign(PostgreSQLParser.A_expr_unary_signContext context)
    {
        if (context.MINUS() is null && context.PLUS() is null)
        {
            return VisitA_expr_at_time_zone(context.a_expr_at_time_zone());
        }

        PostgresBuiltInUnaryOperator op;

        if (context.MINUS() is not null)
        {
            op = PostgresBuiltInUnaryOperator.Negate;
        }
        else if (context.PLUS() is not null)
        {
            op = PostgresBuiltInUnaryOperator.Plus;
        }
        else
        {
            throw new PostgresParseException("Unexpected unary operator");
        }

        if (VisitA_expr_at_time_zone(context.a_expr_at_time_zone()) is not Expression expr)
        {
            throw new PostgresParseException("Unable to parse unary expression");
        }

        return new UnaryExpression(op, expr);
    }

    public override SyntaxNode VisitA_expr_at_time_zone(PostgreSQLParser.A_expr_at_time_zoneContext context)
    {
        if (context.AT() is not null)
        {
            throw new NotImplementedException("AT TIME ZONE not yet supported");
        }

        return VisitA_expr_collate(context.a_expr_collate());
    }

    public override SyntaxNode VisitA_expr_collate(PostgreSQLParser.A_expr_collateContext context)
    {
        if (context.COLLATE() is not null)
        {
            throw new NotImplementedException("COLLATE not yet supported");
        }

        return VisitA_expr_typecast(context.a_expr_typecast());
    }

    private static BinaryExpression VisitBinaryExpression<TNextContext>(
        IEnumerable<IParseTree> children, 
        Func<TNextContext, SyntaxNode?> visitFunc,
        Func<int, PostgresBuiltInBinaryOperator> opLookup)
    {
        var parts = new Queue<object>();

        foreach (var child in children)
        {
            if (child is TNextContext nextExpr)
            {
                if (visitFunc(nextExpr) is not Expression expr)
                {
                    throw new PostgresParseException("Unable to parse binary expression operand");
                }

                parts.Enqueue(expr);
            }
            else if (child is ITerminalNode lexerNode)
            {
                var op = opLookup(lexerNode.Symbol.Type);
                parts.Enqueue(op);   
            }
            else
            {
                throw new PostgresParseException($"Unexpected child of binary operator: {child.GetType()}");
            }
        }

        if (parts.Count < 3)
        {
            throw new PostgresParseException("Somehow ended up with less than two expressions and one operator for a binary operator");
        }

        if (parts.Dequeue() is not Expression startLeft
            || parts.Dequeue() is not PostgresBuiltInBinaryOperator startOp
            || parts.Dequeue() is not Expression startRight)
        {
            throw new PostgresParseException("Unexpected parse order from binary expression");
        }

        var binary = new BinaryExpression(
            startLeft,
            new BuiltInOperator(startOp),
            startRight);

        while (parts.TryDequeue(out var nextPart))
        {
            if (nextPart is not PostgresBuiltInBinaryOperator nextOp
                || !parts.TryDequeue(out var nextNextPart)
                || nextNextPart is not Expression nextExpr)
            {
                throw new PostgresParseException("Unexpected parse order from binary expression");
            }
            
            binary = new BinaryExpression(
                binary, 
                new BuiltInOperator(nextOp),
                nextExpr);
        }
        
        return binary;
    }
}