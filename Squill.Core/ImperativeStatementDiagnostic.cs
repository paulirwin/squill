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
    /// </summary>
    /// <param name="statementName">The statement's leading keywords, upper-cased.</param>
    /// <param name="isDml">
    /// True for a statement that writes data (INSERT/UPDATE/DELETE and friends), which gets a
    /// different remedy. Seed and reference data is a legitimate thing to want and Squill
    /// already supports it through pre/post-deploy scripts, so pointing a seed script at
    /// "express this as CREATE" would be useless advice — there is no CREATE that inserts a row.
    ///
    /// <para>
    /// False for a query (SELECT). A query is rejected just the same — it declares nothing, so
    /// it does not belong in a schema file — but it writes no data, and telling someone to move
    /// a stray SELECT into a deploy script would be advising them to keep a statement that does
    /// nothing either way.
    /// </para>
    /// </param>
    public static string Message(string statementName, bool isDml)
        => isDml
            ? $"{statementName} is not allowed in a declarative Squill project: source files "
              + "declare schema, not data. Move seed and reference data into a pre- or "
              + "post-deploy script, which Squill runs verbatim on every deploy."
            : $"{statementName} is not allowed in a declarative Squill project. Express the "
              + "desired end-state as CREATE and Squill will generate the ALTER/DROP needed to "
              + "reach it during deploy. Imperative SQL that must run as written belongs in a "
              + "pre- or post-deploy script.";

    /// <summary>
    /// Builds the exception for a statement at a known position, anchored to the source file so
    /// the host can point the IDE at the offending statement.
    /// </summary>
    public static SqlSourceException Exception(
        string statementName, bool isDml, string sourceFile, int? line, int? column)
        => new(
            Message(statementName, isDml),
            sourceFile,
            line,
            column,
            SqlSourceException.ImperativeStatement);
}
