namespace Squill.PostgresParser.Syntax;

/// <summary>
/// What an imperative statement in a declarative source file actually does, which decides the
/// remedy its SQ0006 error offers (issue #125). All three are rejected; they differ only in
/// what the author should do instead, and offering the wrong remedy is worse than offering
/// none — it sends them to fix the wrong thing.
/// </summary>
public enum ImperativeKind
{
    /// <summary>
    /// A statement that changes schema: <c>ALTER</c>, <c>DROP</c>, <c>TRUNCATE</c>. The remedy
    /// is to declare the end-state as <c>CREATE</c> and let Squill generate the migration.
    /// </summary>
    SchemaChange,

    /// <summary>
    /// A statement that writes data: <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c>, <c>COPY</c>,
    /// and a data-modifying CTE. The remedy is a pre/post-deploy script — seed and reference
    /// data is a legitimate thing to want, and there is no <c>CREATE</c> that inserts a row.
    /// </summary>
    DataChange,

    /// <summary>
    /// A query: <c>SELECT</c>, <c>VALUES</c>, <c>TABLE</c>, or a read-only CTE. It declares
    /// nothing and writes nothing, so neither of the other remedies fits — pointing it at
    /// <c>CREATE</c> would imply it was trying to express an end-state, and pointing it at a
    /// deploy script would be advising the author to keep a statement that does nothing
    /// either way. The remedy is simply to remove it.
    /// </summary>
    Query,
}
