using Antlr4.Runtime;
using Antlr4.Runtime.Tree;
using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public partial class PostgresVisitor
{
    // Statements that change schema rather than declare it. Matched on the leading keyword,
    // which is what makes this survive a grammar re-vendor: the `stmt` rule has ~67
    // alternatives and their context types move between upstream revisions, but the keyword
    // a user types does not.
    private static readonly HashSet<string> SchemaChangeKeywords =
    [
        "ALTER", "DROP", "TRUNCATE", "RENAME",
    ];

    // Statements that write data. COPY is here too — it loads rows, and Postgres treats it
    // as such.
    private static readonly HashSet<string> DataChangeKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "MERGE", "COPY",
    ];

    // Queries: they neither declare nor change anything.
    private static readonly HashSet<string> QueryKeywords =
    [
        "SELECT", "TABLE", "VALUES",
    ];

    /// <summary>
    /// Maps one top-level statement. Anything the visitor has no mapping for falls through to
    /// the base implementation, which returns null (or a non-statement node) — so this is also
    /// where an imperative statement is recognized and turned into a marker for the model
    /// builder to reject (issue #125).
    /// </summary>
    public override SyntaxNode? VisitStmt(PostgreSQLParser.StmtContext context)
    {
        var mapped = base.VisitStmt(context);

        if (mapped is Statement)
        {
            return mapped;
        }

        // Not a statement the visitor models. If it is imperative, say so precisely; otherwise
        // leave it unmapped for VisitRoot to report as it did before, since "this construct is
        // not supported yet" is the honest description of, say, an unmodeled CREATE POLICY.
        return ClassifyImperative(context) is { } imperative ? At(imperative, context) : mapped;
    }

    // Builds a marker for an imperative statement, or null when the statement is not one.
    private static ImperativeStatement? ClassifyImperative(PostgreSQLParser.StmtContext context)
    {
        var keywords = LeadingKeywords(context, count: 2);

        if (keywords.Count == 0)
        {
            return null;
        }

        var first = keywords[0];

        // A CTE takes the kind of the statement it feeds, not of the WITH itself:
        // `WITH x AS (…) INSERT …` writes data and must get the seed-data remedy, while
        // `WITH x AS (…) SELECT …` is only a query. Deciding on the leading WITH would send a
        // data-modifying CTE to the "express this as CREATE" remedy, which is the wrong fix.
        if (first == "WITH")
        {
            return new ImperativeStatement("WITH", ClassifyCte(context));
        }

        if (DataChangeKeywords.Contains(first))
        {
            // Just the verb: "INSERT INTO" adds nothing, and the object name that follows is
            // noise in a message that already carries a line number.
            return new ImperativeStatement(first, ImperativeKind.DataChange);
        }

        if (QueryKeywords.Contains(first))
        {
            return new ImperativeStatement(first, ImperativeKind.Query);
        }

        if (!SchemaChangeKeywords.Contains(first))
        {
            return null;
        }

        // Two keywords for DDL, so the message names the kind of object: "ALTER TABLE" rather
        // than a bare "ALTER". TRUNCATE's second word is usually the table name, not a keyword,
        // so it is left alone.
        var name = keywords.Count > 1 && first != "TRUNCATE"
            ? $"{first} {keywords[1]}"
            : first;

        return new ImperativeStatement(name, ImperativeKind.SchemaChange);
    }

    // Whether a CTE writes data. Postgres allows INSERT/UPDATE/DELETE both inside a WITH
    // clause (`WITH d AS (DELETE … RETURNING *) …`) and as the statement the CTE feeds, and
    // either makes the statement a data change — so any data-writing keyword anywhere in it
    // is enough. The alternative, matching on parse-tree context types, is what the leading
    // keyword approach deliberately avoids.
    private static ImperativeKind ClassifyCte(PostgreSQLParser.StmtContext context)
        => LeadingKeywords(context, count: int.MaxValue).Any(DataChangeKeywords.Contains)
            ? ImperativeKind.DataChange
            : ImperativeKind.Query;

    // The first <paramref name="count"/> word-shaped tokens of the statement, upper-cased. The
    // lexer runs over a CaseChangingCharStream so token text is already upper, but a quoted
    // identifier is not, and only word-shaped tokens are of interest — punctuation would never
    // be a keyword.
    private static List<string> LeadingKeywords(PostgreSQLParser.StmtContext context, int count)
        => Trees.Descendants(context)
            .OfType<ITerminalNode>()
            .Select(i => i.Symbol.Text)
            .Where(i => !string.IsNullOrWhiteSpace(i) && i.All(char.IsLetter))
            .Take(count)
            .Select(i => i.ToUpperInvariant())
            .ToList();
}
