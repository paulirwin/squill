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

    /// <summary>
    /// The <c>WITH [CASCADED|LOCAL] CHECK OPTION</c> setting, as <c>CASCADED</c> or
    /// <c>LOCAL</c>, or null when the view declares none (issue #208).
    ///
    /// <para>
    /// One property covers both spellings because PostgreSQL stores them as one facet:
    /// measured on 18, the clause form and <c>WITH (check_option='local')</c> both land in
    /// <c>pg_class.reloptions</c> as <c>check_option=local</c>, indistinguishable afterwards.
    /// A bare <c>WITH CHECK OPTION</c> is stored as <c>cascaded</c>, so it is recorded that
    /// way here rather than as a third state.
    /// </para>
    /// </summary>
    public string? CheckOption { get; set; }

    /// <summary>
    /// The <c>security_invoker</c> reloption, or null when the view declares none.
    ///
    /// <para>
    /// Nullable rather than defaulting to false, because the distinction is real: measured on
    /// PostgreSQL 18, writing <c>security_invoker=false</c> records exactly that in
    /// <c>reloptions</c>, where declaring nothing leaves them empty. Folding the explicit
    /// default into "unset" would make a view that declares it re-diff on every deploy.
    /// This is the opposite of the MariaDB family, where the explicit default is
    /// indistinguishable from absent.
    /// </para>
    /// </summary>
    public bool? SecurityInvoker { get; set; }

    /// <summary>
    /// The <c>security_barrier</c> reloption, or null when the view declares none. Recorded
    /// on the same terms as <see cref="SecurityInvoker"/>.
    /// </summary>
    public bool? SecurityBarrier { get; set; }

    /// <summary>
    /// Reloptions written on the view that Squill does not model, in source order. Carried so
    /// the model builder can warn that they will not reach the DACPAC (SQ1002) rather than
    /// letting them vanish silently, which is what issue #208 was about.
    /// </summary>
    public IList<string> UnmodeledOptions { get; } = new List<string>();
}
