namespace Squill.MariaDbParser.Syntax;

/// <summary>
/// The focused MariaDB syntax tree Squill consumes. This models exactly the statements the
/// provider maps to model elements — CREATE TABLE (columns, data types, PK/FK/unique/index
/// constraints), CREATE INDEX, CREATE PROCEDURE and CREATE VIEW — rather than the full grammar.
/// CREATE FUNCTION is parsed only so it can be reported as unsupported. Everything else in
/// a script is ignored by the parser (see <see cref="AntlrMariaDbParser"/>).
/// </summary>
public sealed class Root
{
    public IList<Statement> Statements { get; } = new List<Statement>();
}

/// <summary>
/// Base type for nodes that record where they start in the source text, so later phases
/// (model building, reference validation) can report diagnostics pointing at the source.
/// </summary>
public abstract class SyntaxNode
{
    /// <summary>The 1-based line in the source text where this node starts, or null when not recorded.</summary>
    public int? Line { get; set; }

    /// <summary>The 1-based column in the source text where this node starts, or null when not recorded.</summary>
    public int? Column { get; set; }
}

/// <summary>Base type for a recognized top-level statement.</summary>
public abstract class Statement : SyntaxNode;

/// <summary>An identifier segment, with its unquoted name.</summary>
public sealed class Identifier(string name)
{
    public string Name { get; } = name;

    public override string ToString() => Name;
}

/// <summary>A possibly-qualified object name (e.g. <c>db.table</c> or just <c>table</c>).</summary>
public sealed class QualifiedName(IReadOnlyList<Identifier> segments)
{
    public IReadOnlyList<Identifier> Segments { get; } = segments;

    /// <summary>The final (bare) segment's name — the object's own name.</summary>
    public string Name => Segments[^1].Name;

    public override string ToString() => string.Join('.', Segments.Select(s => s.Name));
}

// ---- CREATE TABLE ----

public sealed class CreateTableStatement(QualifiedName name) : Statement
{
    public QualifiedName Name { get; } = name;

    /// <summary>Column definitions and table-level constraints, in declaration order.</summary>
    public IList<ITableElement> Elements { get; } = new List<ITableElement>();
}

/// <summary>A member of a CREATE TABLE body: a column definition or a table constraint.</summary>
public interface ITableElement;

public sealed class ColumnDefinition(Identifier name, DataType dataType) : ITableElement
{
    public Identifier Name { get; } = name;
    public DataType DataType { get; } = dataType;
    public IList<ColumnConstraint> Constraints { get; } = new List<ColumnConstraint>();
}

// ---- Data types ----

/// <summary>A column data type: a MariaDB built-in type name plus optional modifiers.</summary>
public sealed class DataType(string typeName)
{
    /// <summary>The canonical (lower-cased) MariaDB type name, e.g. <c>int</c>, <c>varchar</c>.</summary>
    public string TypeName { get; } = typeName;

    /// <summary>Numeric modifiers, e.g. the 255 in varchar(255) or 10,2 in decimal(10,2).</summary>
    public IList<long> Modifiers { get; } = new List<long>();

    /// <summary>
    /// The value list of an <c>enum(...)</c> or <c>set(...)</c> type, each entry a
    /// single-quoted literal exactly as written, e.g. <c>'G'</c>. Empty for other types.
    /// </summary>
    public IList<string> CollectionValues { get; } = new List<string>();

    /// <summary>Whether the type was declared UNSIGNED (numeric types only).</summary>
    public bool IsUnsigned { get; set; }
}

// ---- Column constraints ----

public abstract class ColumnConstraint : SyntaxNode;

/// <summary>A CONSTRAINT-named wrapper around another column constraint.</summary>
public sealed class NamedColumnConstraint(string? name, ColumnConstraint constraint) : ColumnConstraint
{
    public string? Name { get; } = name;
    public ColumnConstraint Constraint { get; } = constraint;
}

public sealed class NullableColumnConstraint(bool nullable) : ColumnConstraint
{
    public bool Nullable { get; } = nullable;
}

public sealed class PrimaryKeyColumnConstraint : ColumnConstraint;

public sealed class UniqueKeyColumnConstraint : ColumnConstraint;

public sealed class AutoIncrementColumnConstraint : ColumnConstraint;

/// <summary>A DEFAULT clause carrying the raw literal token as written in source.</summary>
public sealed class DefaultColumnConstraint(string? token) : ColumnConstraint
{
    /// <summary>The raw default token (e.g. <c>5</c>, <c>'active'</c>, <c>CURRENT_TIMESTAMP</c>).</summary>
    public string? Token { get; } = token;
}

/// <summary>An inline column REFERENCES clause (a single-column foreign key).</summary>
public sealed class ForeignKeyColumnConstraint(
    QualifiedName referencedTable,
    Identifier? referencedColumn,
    ReferentialAction? onDelete,
    ReferentialAction? onUpdate) : ColumnConstraint
{
    public QualifiedName ReferencedTable { get; } = referencedTable;
    public Identifier? ReferencedColumn { get; } = referencedColumn;
    public ReferentialAction? OnDelete { get; } = onDelete;
    public ReferentialAction? OnUpdate { get; } = onUpdate;
}

/// <summary>A column constraint Squill recognizes but does not model (COMMENT, COLLATE, …).</summary>
public sealed class IgnoredColumnConstraint : ColumnConstraint;

// ---- Table constraints ----

public abstract class TableConstraint : SyntaxNode, ITableElement;

public sealed class NamedTableConstraint(string? name, TableConstraint constraint) : TableConstraint
{
    public string? Name { get; } = name;
    public TableConstraint Constraint { get; } = constraint;
}

public sealed class PrimaryKeyTableConstraint(IReadOnlyList<Identifier> columns) : TableConstraint
{
    public IReadOnlyList<Identifier> Columns { get; } = columns;
}

public sealed class UniqueKeyTableConstraint(string? indexName, IReadOnlyList<Identifier> columns) : TableConstraint
{
    public string? IndexName { get; } = indexName;
    public IReadOnlyList<Identifier> Columns { get; } = columns;
}

public sealed class ForeignKeyTableConstraint(
    IReadOnlyList<Identifier> columns,
    QualifiedName referencedTable,
    IReadOnlyList<Identifier> referencedColumns,
    ReferentialAction? onDelete,
    ReferentialAction? onUpdate) : TableConstraint
{
    public IReadOnlyList<Identifier> Columns { get; } = columns;
    public QualifiedName ReferencedTable { get; } = referencedTable;
    public IReadOnlyList<Identifier> ReferencedColumns { get; } = referencedColumns;
    public ReferentialAction? OnDelete { get; } = onDelete;
    public ReferentialAction? OnUpdate { get; } = onUpdate;
}

/// <summary>An inline INDEX/KEY declaration inside a CREATE TABLE body.</summary>
public sealed class IndexTableConstraint(
    string? indexName,
    string? indexMethod,
    IReadOnlyList<IndexColumn> columns) : TableConstraint
{
    public string? IndexName { get; } = indexName;
    public string? IndexMethod { get; } = indexMethod;
    public IReadOnlyList<IndexColumn> Columns { get; } = columns;
}

/// <summary>A table constraint Squill recognizes but does not model (CHECK, …).</summary>
public sealed class IgnoredTableConstraint : TableConstraint;

// ---- CREATE INDEX ----

public sealed class CreateIndexStatement(string? name, QualifiedName onTable) : Statement
{
    public string? Name { get; } = name;
    public QualifiedName OnTable { get; } = onTable;
    public bool Unique { get; set; }
    public string? IndexMethod { get; set; }
    public IList<IndexColumn> Columns { get; } = new List<IndexColumn>();
}

/// <summary>A single indexed column: its name plus an optional sort direction.</summary>
public sealed class IndexColumn(Identifier column, bool? isAscending)
{
    public Identifier Column { get; } = column;
    public bool? IsAscending { get; } = isAscending;
}

// ---- CREATE PROCEDURE ----

/// <summary>
/// A <c>CREATE [OR REPLACE] PROCEDURE name(params) [options] body</c> statement.
///
/// The <see cref="Body"/> is held verbatim — exactly the characters the body spans in the
/// source — because that is what MariaDB and MySQL both return from
/// <c>information_schema.ROUTINES.ROUTINE_DEFINITION</c>. Keeping it byte-for-byte lets a
/// model parsed from source hash-match one extracted from a live database without
/// canonicalizing the body.
/// </summary>
public sealed class CreateProcedureStatement(QualifiedName name, bool orReplace) : Statement
{
    public QualifiedName Name { get; } = name;

    /// <summary>
    /// Whether OR REPLACE was written (MariaDB-only syntax). This affects how the procedure
    /// is created, not the desired schema state, so it does not participate in the model.
    /// </summary>
    public bool OrReplace { get; } = orReplace;

    public IList<RoutineParameter> Parameters { get; } = new List<RoutineParameter>();

    /// <summary>The routine body, verbatim as written in the source.</summary>
    public string? Body { get; set; }

    /// <summary>
    /// Whether DETERMINISTIC was written. NOT DETERMINISTIC is the default on both engines.
    /// </summary>
    public bool IsDeterministic { get; set; }

    /// <summary>
    /// The SQL data access clause as the catalog spells it (<c>CONTAINS SQL</c>,
    /// <c>NO SQL</c>, <c>READS SQL DATA</c>, <c>MODIFIES SQL DATA</c>), or null when
    /// unwritten — in which case both engines report <c>CONTAINS SQL</c>.
    /// </summary>
    public string? SqlDataAccess { get; set; }

    /// <summary>
    /// Whether SQL SECURITY INVOKER was written. DEFINER is the default on both engines
    /// (the opposite of PostgreSQL), so the invoker case is the one worth recording.
    /// </summary>
    public bool IsSecurityInvoker { get; set; }
}

/// <summary>
/// A single parameter of a routine — its mode, name and declared type. Unlike PostgreSQL,
/// MariaDB and MySQL always name a routine parameter.
/// </summary>
public sealed class RoutineParameter(Identifier name, ParameterMode mode, DataType dataType)
    : SyntaxNode
{
    public Identifier Name { get; } = name;
    public ParameterMode Mode { get; } = mode;
    public DataType DataType { get; } = dataType;
}

/// <summary>
/// The argument mode of a routine parameter. The default when no mode is written is
/// <see cref="In"/>, which is why it is the first (default) member.
/// </summary>
public enum ParameterMode
{
    In,
    Out,
    InOut,
}

// ---- CREATE VIEW ----

/// <summary>
/// A <c>CREATE [OR REPLACE] VIEW name [(columns)] AS SELECT ...</c> statement.
///
/// Unlike a procedure body, a view's <see cref="Body"/> does <em>not</em> round-trip: both
/// engines rewrite the query when they store it, and they do not even rewrite it the same
/// way — MySQL parenthesizes a WHERE clause where MariaDB does not, and both fully qualify
/// every column with the database name. So the body is carried for scripting only, and a
/// view's identity in the model rests on its name and column list, which
/// <c>information_schema.COLUMNS</c> reports faithfully on both engines. See
/// <c>MariaDbModelFactory.CreateView</c>.
/// </summary>
public sealed class CreateViewStatement(QualifiedName name, bool orReplace) : Statement
{
    public QualifiedName Name { get; } = name;

    /// <summary>
    /// Whether OR REPLACE was written. This affects how the view is created, not the desired
    /// schema state, so it does not participate in the model.
    /// </summary>
    public bool OrReplace { get; } = orReplace;

    /// <summary>
    /// The explicit column list written as <c>CREATE VIEW v (a, b) AS ...</c>, if any. When
    /// present it names the view's columns outright; when empty the names are derived from
    /// the select list.
    /// </summary>
    public IList<Identifier> ColumnNames { get; } = new List<Identifier>();

    /// <summary>The columns the select list produces, in order.</summary>
    public IList<ViewSelectColumn> SelectColumns { get; } = new List<ViewSelectColumn>();

    /// <summary>
    /// The tables the query selects from, in the order written. Used to resolve a
    /// <c>SELECT *</c> against the tables declared in the project.
    /// </summary>
    public IList<QualifiedName> SourceTables { get; } = new List<QualifiedName>();

    /// <summary>The query text, verbatim as written after <c>AS</c>.</summary>
    public string? Body { get; set; }
}

/// <summary>
/// A single entry in a view's select list, reduced to what naming the view's columns needs.
///
/// A view column takes its name from an explicit alias (<c>SELECT id AS the_id</c>), or
/// failing that from the column being selected (<c>SELECT id</c>). An entry that is neither —
/// an unaliased expression such as <c>SELECT qty * 2</c> — has no name Squill can derive, and
/// <see cref="IsWildcard"/> marks a <c>*</c> that must be expanded against the source table.
/// </summary>
public sealed class ViewSelectColumn
{
    private ViewSelectColumn(string? alias, string? columnName, bool isWildcard, string? qualifier)
    {
        Alias = alias;
        ColumnName = columnName;
        IsWildcard = isWildcard;
        Qualifier = qualifier;
    }

    /// <summary>An explicit alias, if one was written.</summary>
    public string? Alias { get; }

    /// <summary>The selected column's own name, when the entry is a plain column reference.</summary>
    public string? ColumnName { get; }

    /// <summary>Whether this entry is a <c>*</c> wildcard.</summary>
    public bool IsWildcard { get; }

    /// <summary>The table qualifier on a wildcard or column reference, if one was written.</summary>
    public string? Qualifier { get; }

    /// <summary>
    /// The name this entry gives the view's column, or null when none can be derived.
    /// </summary>
    public string? DerivedName => Alias ?? ColumnName;

    public static ViewSelectColumn Named(string columnName, string? qualifier = null)
        => new(alias: null, columnName, isWildcard: false, qualifier);

    public static ViewSelectColumn Aliased(string alias)
        => new(alias, columnName: null, isWildcard: false, qualifier: null);

    public static ViewSelectColumn Wildcard(string? qualifier = null)
        => new(alias: null, columnName: null, isWildcard: true, qualifier);

    /// <summary>An entry with no derivable name, e.g. an unaliased <c>qty * 2</c>.</summary>
    public static ViewSelectColumn Unnamed()
        => new(alias: null, columnName: null, isWildcard: false, qualifier: null);
}

// ---- CREATE FUNCTION ----

/// <summary>
/// A <c>CREATE [OR REPLACE] FUNCTION name(params) RETURNS type [options] body</c> statement
/// (issue #74). Mirrors <see cref="CreateProcedureStatement"/> — a stored function is a
/// routine like a procedure, but takes only <c>IN</c> parameters and declares a return type.
///
/// The <see cref="Body"/> is held verbatim, for the same reason as a procedure's: both
/// engines return <c>ROUTINE_DEFINITION</c> byte-for-byte, and it is the <c>RETURN ...</c>
/// or <c>BEGIN ... END</c> that follows the <c>RETURNS</c> clause — the return type itself is
/// carried separately in <see cref="ReturnType"/>, not in the body.
/// </summary>
public sealed class CreateFunctionStatement(QualifiedName name, DataType returnType, bool orReplace)
    : Statement
{
    public QualifiedName Name { get; } = name;

    /// <summary>The declared return type (the type after <c>RETURNS</c>).</summary>
    public DataType ReturnType { get; } = returnType;

    /// <summary>
    /// Whether OR REPLACE was written (MariaDB-only syntax). This affects how the function is
    /// created, not the desired schema state, so it does not participate in the model.
    /// </summary>
    public bool OrReplace { get; } = orReplace;

    /// <summary>The function's parameters. A function parameter is always <c>IN</c>.</summary>
    public IList<RoutineParameter> Parameters { get; } = new List<RoutineParameter>();

    /// <summary>The routine body, verbatim as written in the source.</summary>
    public string? Body { get; set; }

    /// <summary>
    /// Whether DETERMINISTIC was written. NOT DETERMINISTIC is the default on both engines.
    /// </summary>
    public bool IsDeterministic { get; set; }

    /// <summary>
    /// The SQL data access clause as the catalog spells it (<c>CONTAINS SQL</c>,
    /// <c>NO SQL</c>, <c>READS SQL DATA</c>, <c>MODIFIES SQL DATA</c>), or null when
    /// unwritten — in which case both engines report <c>CONTAINS SQL</c>.
    /// </summary>
    public string? SqlDataAccess { get; set; }

    /// <summary>
    /// Whether SQL SECURITY INVOKER was written. DEFINER is the default on both engines.
    /// </summary>
    public bool IsSecurityInvoker { get; set; }
}

/// <summary>
/// A DDL statement the parser recognized but Squill does not model (<c>CREATE VIEW</c>, a
/// <c>CREATE TABLE ... AS SELECT</c>, <c>ALTER</c>, <c>DROP</c>, …). It is carried into the
/// syntax tree as a marker rather than dropped so the model builder can warn that the
/// construct will not reach the DACPAC, at its source position (issue #61).
/// </summary>
public sealed class UnmodeledStatement(string description) : Statement
{
    /// <summary>A short description of the construct, e.g. <c>CREATE VIEW</c>.</summary>
    public string Description { get; } = description;
}
