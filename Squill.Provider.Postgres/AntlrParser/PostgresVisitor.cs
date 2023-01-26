using Squill.Provider.Postgres.Syntax;

// ReSharper disable IdentifierTypo

namespace Squill.Provider.Postgres.AntlrParser;

public class PostgresVisitor : PostgresParserBaseVisitor<SyntaxNode?>
{
    public override SyntaxNode VisitRoot(PostgresParser.RootContext context)
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

    public override SyntaxNode? VisitCreatestmt(PostgresParser.CreatestmtContext context)
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

    public override SyntaxNode VisitQualified_name(PostgresParser.Qualified_nameContext context)
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

    public override SyntaxNode VisitTableelement(PostgresParser.TableelementContext context)
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

        var columnDef = new ColumnDefinition(name, dataType);

        return columnDef;
    }

    public override SyntaxNode VisitTypename(PostgresParser.TypenameContext context)
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
    
        throw new NotImplementedException($"Support for {simpletypenameContext.GetText()} type name not yet implemented");
    }
}