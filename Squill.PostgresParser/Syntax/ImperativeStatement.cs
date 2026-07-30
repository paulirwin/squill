namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A statement that changes state imperatively rather than declaring it — an <c>ALTER</c>,
/// <c>DROP</c> or <c>TRUNCATE</c>, or DML such as <c>INSERT</c>. It has no meaning in a
/// declarative project, so it is carried as a marker for the model builder to reject with a
/// purpose-built SQ0006 error (issue #125).
///
/// <para>
/// A marker rather than a throw from the visitor, because the position is the point: throwing
/// during the parse aborts the whole file and loses the line and column, which is exactly how
/// this used to surface — an SQ0001 reading "Expected VisitStmt to return a Statement" with no
/// position at all. Carrying it as a statement lets the builder anchor the error to it and
/// keep reporting the rest of the file.
/// </para>
/// </summary>
public class ImperativeStatement(string name, bool isDml) : Statement
{
    /// <summary>The statement's leading keywords, upper-cased — <c>ALTER TABLE</c>, <c>DROP INDEX</c>.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// True for DML, which is rejected with a different remedy: seed data belongs in a
    /// post-deploy script, and there is no CREATE that inserts a row.
    /// </summary>
    public bool IsDml { get; } = isDml;
}
