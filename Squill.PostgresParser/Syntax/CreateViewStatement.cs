namespace Squill.PostgresParser.Syntax;

/// <summary>
/// A <c>CREATE [OR REPLACE] VIEW name [(columns)] AS SELECT ...</c> statement.
///
/// Unlike a procedure body, a view's <see cref="Body"/> does <em>not</em> round-trip: every
/// engine rewrites the query it is given. PostgreSQL returns a reformatted query from
/// <c>pg_get_viewdef</c>, and MariaDB/MySQL rewrite it further still (fully qualifying every
/// column and embedding the database name). So the body is carried for scripting only, and a
/// view's identity in the model rests on its name and column list — both of which the
/// catalog reports faithfully. See <c>PostgresModelFactory.CreateView</c>.
/// </summary>
public class CreateViewStatement : Statement
{
    public CreateViewStatement(QualifiedName name, bool orReplace)
    {
        Name = name;
        OrReplace = orReplace;
    }

    public QualifiedName Name { get; }

    /// <summary>
    /// Whether OR REPLACE was written. This affects how the view is created, not the desired
    /// schema state, so it does not participate in the model.
    /// </summary>
    public bool OrReplace { get; }

    /// <summary>
    /// The explicit column list written as <c>CREATE VIEW v (a, b) AS ...</c>, if any. When
    /// present it names the view's columns outright; when empty the names are derived from
    /// the select list (see <see cref="SelectColumns"/>).
    /// </summary>
    public IList<Identifier> ColumnNames { get; } = new List<Identifier>();

    /// <summary>
    /// The columns the select list produces, in order — the raw material for naming the
    /// view's columns when no explicit column list is written.
    /// </summary>
    public IList<ViewSelectColumn> SelectColumns { get; } = new List<ViewSelectColumn>();

    /// <summary>
    /// The tables the query selects from, in the order written. Used to resolve a
    /// <c>SELECT *</c> against the tables declared in the project.
    /// </summary>
    public IList<QualifiedName> SourceTables { get; } = new List<QualifiedName>();

    /// <summary>The query text, verbatim as written after <c>AS</c>.</summary>
    public string? Body { get; set; }
}
