namespace Squill.Core;

/// <summary>
/// Builds the <see cref="SqlSourceException.ImperativeStatement"/> (SQ0006) error for an
/// imperative statement authored in a declarative source file (issue #125).
///
/// <para>
/// Shared by both providers so the same mistake reads the same whichever engine a project
/// targets. The providers detect it differently — Postgres maps the statement to a marker
/// syntax node, MariaDB already had one — but what the author sees should not depend on that.
/// </para>
/// </summary>
public static class ImperativeStatementDiagnostic
{
    /// <summary>
    /// The message for <paramref name="statementName"/> (e.g. <c>ALTER TABLE</c>, <c>INSERT</c>),
    /// which is quoted back so the author can find the statement even without the line number.
    /// The remedy varies by <paramref name="kind"/>: offering the wrong one is worse than
    /// offering none, since it sends the author to fix the wrong thing.
    /// </summary>
    /// <param name="statementName">The statement's leading keywords, upper-cased.</param>
    /// <param name="kind">What the statement does, and so what should replace it.</param>
    public static string Message(string statementName, ImperativeStatementKind kind)
        => kind switch
        {
            ImperativeStatementKind.DataChange =>
                $"{statementName} is not allowed in a declarative Squill project: source files "
                + "declare schema, not data. Move seed and reference data into a pre- or "
                + "post-deploy script, which Squill runs verbatim on every deploy.",

            ImperativeStatementKind.Query =>
                $"{statementName} is not allowed in a declarative Squill project: source files "
                + "declare the schema Squill should deploy, and a query neither declares nor "
                + "changes anything. Remove it, or move it into a pre- or post-deploy script if "
                + "it needs to run during deployment.",

            _ =>
                $"{statementName} is not allowed in a declarative Squill project. Express the "
                + "desired end-state as CREATE and Squill will generate the ALTER/DROP needed to "
                + "reach it during deploy. Imperative SQL that must run as written belongs in a "
                + "pre- or post-deploy script.",
        };

    /// <summary>
    /// Builds the exception for a statement at a known position, anchored to the source file so
    /// the host can point the IDE at the offending statement.
    /// </summary>
    public static SqlSourceException Exception(
        string statementName,
        ImperativeStatementKind kind,
        string sourceFile,
        int? line,
        int? column)
        => new(
            Message(statementName, kind),
            sourceFile,
            line,
            column,
            SqlSourceException.ImperativeStatement);
}
