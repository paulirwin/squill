namespace Squill.MariaDbParser.Syntax;

/// <summary>
/// The focused MariaDB syntax tree Squill consumes. This models exactly the statements the
/// provider maps to model elements — CREATE TABLE (columns, data types, PK/FK/unique/index
/// constraints), CREATE INDEX, CREATE PROCEDURE, CREATE FUNCTION, CREATE TRIGGER and CREATE
/// VIEW — rather than the full grammar. Everything else in a script is ignored by the parser
/// (see <see cref="AntlrMariaDbParser"/>).
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
public sealed class DefaultColumnConstraint(string? token, string? onUpdateToken = null)
    : ColumnConstraint
{
    /// <summary>The raw default token (e.g. <c>5</c>, <c>'active'</c>, <c>CURRENT_TIMESTAMP</c>).</summary>
    public string? Token { get; } = token;

    /// <summary>
    /// The raw token of a trailing <c>ON UPDATE</c> clause, which refreshes the column on every
    /// row update — or <c>null</c> if the default had none. The grammar makes this part of the
    /// same <c>defaultValue</c> production as the default itself
    /// (<c>currentTimestamp (ON UPDATE currentTimestamp)?</c>), so it is surfaced here rather
    /// than as a separate constraint.
    ///
    /// Kept as the written token rather than a flag: the rule admits a fractional-seconds
    /// precision (<c>CURRENT_TIMESTAMP(3)</c>) and several function spellings, so which of them
    /// can be modeled is a provider decision.
    /// </summary>
    public string? OnUpdateToken { get; } = onUpdateToken;
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

/// <summary>
/// An inline column-level <c>CHECK (expr)</c> constraint (issue #120). The predicate is
/// carried as the source text between the parentheses; MariaDB rewrites it when it stores
/// it, so it is reproduced for scripting rather than compared.
/// </summary>
public sealed class CheckColumnConstraint(string expression) : ColumnConstraint
{
    public string Expression { get; } = expression;
}

/// <summary>
/// A generated (computed) column: <c>GENERATED ALWAYS AS (expr) STORED|VIRTUAL</c>
/// (issue #120). MariaDB defaults to VIRTUAL when no storage kind is written, and accepts
/// PERSISTENT as a synonym for STORED.
/// </summary>
public sealed class GeneratedColumnConstraint(string expression, bool isStored) : ColumnConstraint
{
    public string Expression { get; } = expression;

    /// <summary>True for STORED/PERSISTENT; false for VIRTUAL (the MariaDB default).</summary>
    public bool IsStored { get; } = isStored;
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

// A PRIMARY KEY and a UNIQUE key are index declarations in their own right, so their keys are
// IndexColumns rather than bare names: both accept a prefix length, and in a PRIMARY KEY it
// decides which rows the table accepts as unique (issue #161).
public sealed class PrimaryKeyTableConstraint(IReadOnlyList<IndexColumn> columns) : TableConstraint
{
    public IReadOnlyList<IndexColumn> Columns { get; } = columns;
}

public sealed class UniqueKeyTableConstraint(string? indexName, IReadOnlyList<IndexColumn> columns) : TableConstraint
{
    public string? IndexName { get; } = indexName;
    public IReadOnlyList<IndexColumn> Columns { get; } = columns;
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
    IReadOnlyList<IndexColumn> columns,
    string? indexKind = null) : TableConstraint
{
    public string? IndexName { get; } = indexName;
    public string? IndexMethod { get; } = indexMethod;
    public IReadOnlyList<IndexColumn> Columns { get; } = columns;

    /// <summary>
    /// The index kind written as a leading keyword — <c>FULLTEXT</c> or <c>SPATIAL</c> — or
    /// <c>null</c> for an ordinary index (issue #146).
    ///
    /// Distinct from <see cref="IndexMethod"/>, which is the <c>USING</c> access method
    /// (<c>BTREE</c>/<c>HASH</c>). The two are not interchangeable: both engines reject
    /// <c>USING FULLTEXT</c> as a syntax error, and the kind must be written as a prefix
    /// (<c>CREATE FULLTEXT INDEX …</c>) instead.
    /// </summary>
    public string? IndexKind { get; } = indexKind;
}

/// <summary>
/// A table-level <c>CHECK (expr)</c> constraint (issue #120). Unlike a PK or UNIQUE it has
/// no column set of its own — the predicate may reference any columns of the table.
/// </summary>
public sealed class CheckTableConstraint(string expression) : TableConstraint
{
    public string Expression { get; } = expression;
}

/// <summary>A table constraint Squill recognizes but does not model (FULLTEXT, SPATIAL, …).</summary>
public sealed class IgnoredTableConstraint : TableConstraint;

// ---- CREATE INDEX ----

public sealed class CreateIndexStatement(string? name, QualifiedName onTable) : Statement
{
    public string? Name { get; } = name;
    public QualifiedName OnTable { get; } = onTable;
    public bool Unique { get; set; }
    public string? IndexMethod { get; set; }

    /// <summary>
    /// The index kind written before <c>INDEX</c> — <c>FULLTEXT</c> or <c>SPATIAL</c> — or
    /// <c>null</c> for an ordinary index (issue #146). See
    /// <see cref="IndexTableConstraint.IndexKind"/> for why this is separate from
    /// <see cref="IndexMethod"/>.
    /// </summary>
    public string? IndexKind { get; set; }

    public IList<IndexColumn> Columns { get; } = new List<IndexColumn>();
}

/// <summary>
/// A single index key: a column (optionally with a prefix length) or an expression, plus an
/// optional sort direction. The grammar rule is
/// <c>((uid | STRING_LITERAL) ('(' decimalLiteral ')')? | expression) sortType?</c>, so exactly
/// one of <see cref="Column"/> and <see cref="KeyExpression"/> is set.
/// </summary>
public sealed class IndexColumn(
    Identifier? column,
    bool? isAscending,
    int? prefixLength = null,
    string? keyExpression = null)
{
    /// <summary>The indexed column, or <c>null</c> when this key is an expression.</summary>
    public Identifier? Column { get; } = column;

    public bool? IsAscending { get; } = isAscending;

    /// <summary>
    /// The declared prefix length — the <c>20</c> in <c>Brand(20)</c> — or <c>null</c> for a
    /// whole-column key (issue #161).
    ///
    /// Not an optimization detail: a prefix is <em>mandatory</em> for indexing a TEXT or BLOB
    /// column on MySQL, and inside a PRIMARY KEY it decides which rows the table accepts as
    /// unique, so dropping it changes what the schema means.
    /// </summary>
    public int? PrefixLength { get; } = prefixLength;

    /// <summary>
    /// The source text of a functional index key — the <c>a + b</c> in
    /// <c>CREATE INDEX ix ON t ((a + b))</c> — or <c>null</c> when this key names a column
    /// (issue #161). Carried verbatim; only MySQL supports these, MariaDB rejects the DDL.
    /// </summary>
    public string? KeyExpression { get; } = keyExpression;
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

// ---- CREATE TRIGGER ----

/// <summary>
/// A <c>CREATE [OR REPLACE] TRIGGER name {BEFORE|AFTER} {INSERT|UPDATE|DELETE} ON table
/// FOR EACH ROW body</c> statement (issue #100). A trigger is a routine-like object bound to
/// a table: it fires at a given <see cref="Timing"/> for a given <see cref="Event"/>, running
/// its <see cref="Body"/> for each affected row.
///
/// The <see cref="Body"/> is held verbatim — exactly the characters it spans in the source —
/// because both engines return it that way from <c>information_schema.TRIGGERS.ACTION_STATEMENT</c>.
/// Keeping it byte-for-byte lets a model parsed from source hash-match one extracted from a
/// live database without canonicalizing the body.
/// </summary>
public sealed class CreateTriggerStatement(
    QualifiedName name,
    string timing,
    string @event,
    QualifiedName table,
    bool orReplace) : Statement
{
    public QualifiedName Name { get; } = name;

    /// <summary>When the trigger fires: <c>BEFORE</c> or <c>AFTER</c> (upper-cased).</summary>
    public string Timing { get; } = timing;

    /// <summary>The event it fires on: <c>INSERT</c>, <c>UPDATE</c> or <c>DELETE</c> (upper-cased).</summary>
    public string Event { get; } = @event;

    /// <summary>The table the trigger is defined on.</summary>
    public QualifiedName Table { get; } = table;

    /// <summary>
    /// Whether OR REPLACE was written (MariaDB-only syntax). This affects how the trigger is
    /// created, not the desired schema state, so it does not participate in the model.
    /// </summary>
    public bool OrReplace { get; } = orReplace;

    /// <summary>The trigger body, verbatim as written in the source.</summary>
    public string? Body { get; set; }
}

// ---- CREATE EVENT ----

/// <summary>
/// A <c>CREATE EVENT name ON SCHEDULE ... DO body</c> statement (issue #122). An event is a
/// scheduled routine: unlike a trigger it is bound to a clock rather than a table.
///
/// The schedule is modeled the way <c>information_schema.EVENTS</c> reports it, not the way
/// it is written, so a model parsed from source hash-matches one extracted from a live
/// database. A one-shot <c>AT</c> event is <see cref="EventType"/> <c>ONE TIME</c> and carries
/// <see cref="ExecuteAt"/>; a recurring <c>EVERY</c> event is <c>RECURRING</c> and carries
/// <see cref="IntervalValue"/>/<see cref="IntervalField"/> plus <see cref="Starts"/> and
/// optionally <see cref="Ends"/>.
///
/// Two forms the engines accept are deliberately rejected by the mapper, because the catalog
/// resolves both against the wall clock at creation time and so they could never round-trip:
/// a recurring event with no <c>STARTS</c> (the catalog synthesizes one from "now"), and an
/// <c>AT</c> whose value is not a constant (e.g. <c>CURRENT_TIMESTAMP + INTERVAL 1 DAY</c>).
/// </summary>
public sealed class CreateEventStatement(QualifiedName name, string eventType) : Statement
{
    public QualifiedName Name { get; } = name;

    /// <summary>
    /// <c>ONE TIME</c> for the <c>AT</c> form or <c>RECURRING</c> for the <c>EVERY</c> form,
    /// matching <c>information_schema.EVENTS.EVENT_TYPE</c>.
    /// </summary>
    public string EventType { get; } = eventType;

    /// <summary>The one-shot execution time, for the <c>AT</c> form; null for a recurring event.</summary>
    public string? ExecuteAt { get; set; }

    /// <summary>
    /// The recurrence interval's value, as the catalog reports it. A compound interval is
    /// space-separated (<c>EVERY '2:3' DAY_HOUR</c> is reported as <c>2 3</c>).
    /// </summary>
    public string? IntervalValue { get; set; }

    /// <summary>The recurrence interval's unit (<c>DAY</c>, <c>HOUR</c>, …), upper-cased.</summary>
    public string? IntervalField { get; set; }

    /// <summary>The recurrence start time. Required for a recurring event; null for a one-shot.</summary>
    public string? Starts { get; set; }

    /// <summary>The recurrence end time, when <c>ENDS</c> was written.</summary>
    public string? Ends { get; set; }

    /// <summary>
    /// Whether <c>ON COMPLETION PRESERVE</c> was written — i.e. the event survives after its
    /// last run rather than dropping itself. <c>NOT PRESERVE</c> is the default on both engines.
    /// </summary>
    public bool PreserveOnCompletion { get; set; }

    /// <summary>
    /// The event's status as the catalog reports it: <c>ENABLED</c> (the default),
    /// <c>DISABLED</c>, or <c>SLAVESIDE_DISABLED</c> for <c>DISABLE ON SLAVE</c>.
    /// </summary>
    public string Status { get; set; } = "ENABLED";

    /// <summary>The <c>COMMENT</c> text, unquoted; null when none was written.</summary>
    public string? Comment { get; set; }

    /// <summary>The event body, verbatim as written in the source.</summary>
    public string? Body { get; set; }
}

/// <summary>
/// A DDL statement the parser recognized but Squill does not model (a
/// <c>CREATE TABLE ... AS SELECT</c>, and other declarations awaiting support). It is carried
/// into the syntax tree as a marker rather than dropped so the model builder can warn that the
/// construct will not reach the DACPAC, at its source position (issue #61).
///
/// <para>
/// <c>ALTER</c>/<c>DROP</c> used to land here too. They are <see cref="ImperativeStatement"/>
/// now: "not modeled by Squill" describes a gap Squill might one day fill, which is the wrong
/// thing to tell someone whose real problem is that the statement does not belong in a
/// declarative project at all (issue #125).
/// </para>
/// </summary>
public sealed class UnmodeledStatement(string description) : Statement
{
    /// <summary>A short description of the construct, e.g. <c>CREATE VIEW</c>.</summary>
    public string Description { get; } = description;
}

/// <summary>
/// A statement that changes state imperatively rather than declaring it — an <c>ALTER</c>,
/// <c>DROP</c> or <c>TRUNCATE</c>, or DML such as <c>INSERT</c>. It has no meaning in a
/// declarative project, so it is carried as a marker for the model builder to reject with a
/// purpose-built SQ0006 error (issue #125).
/// </summary>
public sealed class ImperativeStatement(string name, bool isDml) : Statement
{
    /// <summary>The statement's leading keywords, upper-cased — <c>ALTER TABLE</c>, <c>DROP INDEX</c>.</summary>
    public string Name { get; } = name;

    /// <summary>
    /// True for DML, which is rejected with a different remedy: seed data belongs in a
    /// post-deploy script, and there is no CREATE that inserts a row.
    /// </summary>
    public bool IsDml { get; } = isDml;
}
