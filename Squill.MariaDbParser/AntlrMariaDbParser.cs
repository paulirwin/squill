using Antlr4.Runtime;
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
        var parser = new MariaDBParser(tokenStream);

        // ANTLR's default listeners print errors to the console; collect them instead so a
        // syntax error surfaces as a MariaDbParseException carrying the error's 1-based
        // line/column, which hosts (e.g. the MSBuild task) report as source diagnostics.
        var errors = new SyntaxErrorCollectingListener();
        lexer.RemoveErrorListeners();
        lexer.AddErrorListener(errors);
        parser.RemoveErrorListeners();
        parser.AddErrorListener(errors);

        var rootContext = parser.root();

        if (errors.Errors.Count > 0)
        {
            // Report the first error: with recovery a single mistake often cascades, so
            // later entries are usually noise. The mapper must not run over a tree with
            // error nodes — it would fail on the missing pieces with worse messages.
            var (message, line, column) = errors.Errors[0];

            throw new MariaDbParseException($"Syntax error: {message}", line, column);
        }

        var root = new Root();

        foreach (var mapped in EnumerateStatements(rootContext))
        {
            root.Statements.Add(mapped);
        }

        return root;
    }

    // Walks the root -> sqlStatements -> sqlStatement chain and maps each statement Squill has
    // something to say about: DDL, and DML so an authored INSERT can be rejected. Everything
    // else (SET, transaction control, …) is still skipped.
    private static IEnumerable<Statement> EnumerateStatements(
        MariaDBParser.RootContext rootContext)
    {
        var sqlStatements = rootContext.sqlStatements();

        if (sqlStatements is null)
        {
            yield break;
        }

        foreach (var sqlStatement in sqlStatements.sqlStatement())
        {
            if (sqlStatement.ddlStatement() is { } ddl)
            {
                if (MariaDbStatementMapper.Map(ddl) is { } mappedDdl)
                {
                    yield return mappedDdl;
                }

                continue;
            }

            // DML used to be dropped here, so a stray INSERT in a source file vanished with no
            // diagnostic whatsoever — the build succeeded and the statement simply never
            // happened. It is now carried through to be rejected as SQ0006 (issue #125).
            if (sqlStatement.dmlStatement() is { } dml)
            {
                yield return MariaDbStatementMapper.Map(dml);
            }
        }
    }
}
