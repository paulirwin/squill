namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A statement that changes state imperatively rather than declaring it — an <c>ALTER</c>,
/// <c>DROP</c> or <c>TRUNCATE</c>, DML such as <c>INSERT</c>, or a bare query. None of them
/// have meaning in a declarative project, so the statement is carried as a marker for the
/// model builder to reject with a purpose-built SQ0006 error (issue #125).
///
/// <para>
/// A marker rather than a throw from the visitor, because the position is the point: throwing
/// during the parse aborts the whole file and loses the line and column, which is exactly how
/// this used to surface — an SQ0001 reading "Expected VisitStmt to return a Statement" with no
/// position at all. Carrying it as a statement lets the builder anchor the error to it and
/// keep reporting the rest of the file.
/// </para>
/// </summary>
public class ImperativeStatement(string name, ImperativeKind kind) : Statement
{
    /// <summary>The statement's leading keywords, upper-cased — <c>ALTER TABLE</c>, <c>DROP INDEX</c>.</summary>
    public string Name { get; } = name;

    /// <summary>What the statement does, and so which remedy its error offers.</summary>
    public ImperativeKind Kind { get; } = kind;
}
