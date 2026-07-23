using Antlr4.Runtime.Tree;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
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
                ? VisitIconst(iconst) as Expression ??
                  throw new PostgresParseException("Unable to parse character length expression")
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
                throw new NotImplementedException(
                    "Support for modifiers on TIME and TIMESTAMP types not yet implemented");
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

            // Type modifiers (e.g. the dimension in vector(3)) are only meaningful for
            // custom/unresolved types here; built-in generic types below don't take
            // modifiers via this path. Parse them once and attach to the resulting type.
            var typeModifiers = new List<Expression>();

            if (generictype.opt_type_modifiers()?.expr_list() is { } modifierList)
            {
                foreach (var modifierExpr in modifierList.a_expr())
                {
                    if (VisitA_expr(modifierExpr) is not Expression modifierExpression)
                    {
                        throw new PostgresParseException("Unable to parse type modifier expression");
                    }

                    typeModifiers.Add(modifierExpression);
                }
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

            if (typeModifiers.Count > 0)
            {
                if (dataType is not UnresolvedDataType)
                {
                    throw new NotImplementedException(
                        $"Type modifiers are not yet supported for type {generictype.GetText()}");
                }

                foreach (var modifier in typeModifiers)
                {
                    dataType.Modifiers.Add(modifier);
                }
            }
        }

        if (simpletypenameContext.bit() is { } bit)
        {
            // A bare `bit` / `bit(n)` is fixed-length; `bit varying` / `bit varying(n)`
            // (aka varbit) is unbounded unless a length is given. The optional length is
            // an expr_list on bitwithlength.
            // See https://www.postgresql.org/docs/current/datatype-bit.html.
            var varying = bit.bitwithlength()?.opt_varying()?.VARYING() is not null
                          || bit.bitwithoutlength()?.opt_varying()?.VARYING() is not null;

            var bitType = new BuiltInDataType(
                varying ? PostgresBuiltInDataType.BitVarying : PostgresBuiltInDataType.Bit,
                bit.GetText());

            if (bit.bitwithlength()?.expr_list() is { } bitModifiers)
            {
                foreach (var bitModifierExpr in bitModifiers.a_expr())
                {
                    if (VisitA_expr(bitModifierExpr) is not Expression bitExpression)
                    {
                        throw new PostgresParseException("Unable to parse bit type length expression");
                    }

                    bitType.Modifiers.Add(bitExpression);
                }
            }

            dataType = bitType;
        }

        if (simpletypenameContext.constinterval() is not null)
        {
            // `interval` optionally carries a field spec (e.g. `interval day to second`)
            // or a fractional-seconds precision (e.g. `interval(6)`); both are captured as
            // the type's original text but the canonical type is always `interval`, which
            // is how format_type() renders a bare interval column.
            // See https://www.postgresql.org/docs/current/datatype-datetime.html#DATATYPE-INTERVAL-INPUT.
            dataType = new BuiltInDataType(PostgresBuiltInDataType.Interval, simpletypenameContext.GetText());
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
}