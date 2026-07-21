using Antlr4.Runtime;
using Antlr4.Runtime.Misc;
using Squill.MariaDbParser.Syntax;

namespace Squill.MariaDbParser;

/// <summary>
/// Parses declarative MariaDB SQL into Squill's focused <see cref="Root"/> syntax tree,
/// using the ANTLR-generated MariaDB grammar. Only the statements Squill models — CREATE
/// TABLE and CREATE INDEX — are mapped; other statements in a script are ignored.
/// </summary>
public class AntlrMariaDbParser : IMariaDbParser
{
    public Root Parse(string text)
    {
        var input = new AntlrInputStream(text);
        var lexer = new MariaDBLexer(input);
        var tokenStream = new CommonTokenStream(lexer);
        var parser = new MariaDBParser(tokenStream)
        {
            ErrorHandler = new BailErrorStrategy(),
        };

        MariaDBParser.RootContext rootContext;

        try
        {
            rootContext = parser.root();
        }
        catch (ParseCanceledException ex)
        {
            throw new MariaDbParseException("Failed to parse MariaDB SQL.", ex);
        }

        var root = new Root();

        foreach (var statement in EnumerateStatements(rootContext))
        {
            var mapped = MariaDbStatementMapper.Map(statement);

            if (mapped is not null)
            {
                root.Statements.Add(mapped);
            }
        }

        return root;
    }

    // Walks the root -> sqlStatements -> sqlStatement -> ddlStatement chain and yields each
    // DDL statement context, skipping anything that isn't a recognized CREATE.
    private static IEnumerable<MariaDBParser.DdlStatementContext> EnumerateStatements(
        MariaDBParser.RootContext rootContext)
    {
        var sqlStatements = rootContext.sqlStatements();

        if (sqlStatements is null)
        {
            yield break;
        }

        foreach (var sqlStatement in sqlStatements.sqlStatement())
        {
            var ddl = sqlStatement.ddlStatement();

            if (ddl is not null)
            {
                yield return ddl;
            }
        }
    }
}
