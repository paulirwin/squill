using Squill.Core;
using Squill.PostgresParser;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Fast, Docker-free tests over the pure model-to-SQL generation. Input models are
/// built with the parser-based model builder (no database required) and diffed
/// against an empty target so every element becomes a CreateDelta.
/// </summary>
public class PostgresScriptGeneratorTests
{
    private static async Task<SchemaComparison> CompareToEmptyAsync(string sql)
    {
        var parser = new AntlrPostgresParser();
        var workspace = new Workspace();
        workspace.Files.Add(new InMemoryStringFile("Test.sql", FileKind.Compile, sql));

        var model = (await new ParserWorkspaceModelBuilder(workspace, parser).ExtractModelAsync()).Model;

        var provider = new PostgresDatabaseProvider("Host=unused");
        return SchemaCompare.Compare(provider, model, new Model());
    }

    [Fact]
    public async Task GenerateScript_SeparatesStepsWithABlankLine()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE EXTENSION citext;
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // Steps are separated by a blank line so the generated (or previewed) script is
        // easier to read — the first statement and the CREATE that follows it must be
        // separated by a blank line rather than running together on adjacent lines.
        var newline = Environment.NewLine;
        Assert.Contains($"{newline}{newline}CREATE ", sql);
    }

    [Fact]
    public async Task GenerateScript_CreateTable()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("CREATE TABLE \"film\"", sql);
        Assert.Contains("NOT NULL", sql);
        Assert.Contains("varchar(255)", sql);
        // The parser now emits the PK as a first-class element, so it scripts.
        Assert.Contains("PRIMARY KEY", sql);
    }

    [Fact]
    public async Task GenerateScript_BareVarchar_ScriptsWithoutLengthOrMax()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE notes
(
    body varchar
);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        // A length-less varchar must script as plain `varchar`; `varchar(MAX)` is
        // SQL-Server syntax and is not valid Postgres.
        Assert.Contains("varchar", sql);
        Assert.DoesNotContain("MAX", sql);
        Assert.DoesNotContain("varchar(", sql);
    }

    [Fact]
    public async Task GenerateScript_Numeric_ScriptsWithPrecisionAndScale()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE prices
(
    amount numeric(12, 2) NOT NULL
);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        // A numeric(p, s) column must script with its precision and scale so the
        // deployed column has the declared type (issue #33).
        Assert.Contains("numeric(12, 2)", sql);
    }

    [Fact]
    public async Task GenerateScript_CreateTableWithIndex()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("CREATE INDEX \"idx_film_title\" ON \"film\"", sql);
        Assert.Contains("(\"title\")", sql);
        // A plain btree index carries the btree defaults in the model, but the generated
        // DDL suppresses the redundant "USING btree" and "NULLS LAST" so it stays clean.
        Assert.DoesNotContain("USING btree", sql);
        Assert.DoesNotContain("NULLS", sql);
    }

    [Fact]
    public async Task GenerateScript_PartialIndex_EmitsWhereClause()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE users
(
    id    integer PRIMARY KEY,
    email varchar(255)
);

CREATE INDEX idx_users_email ON users (email) WHERE email IS NOT NULL;
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CREATE INDEX \"idx_users_email\" ON \"users\"", sql);
        Assert.Contains("(\"email\")", sql);
        Assert.Contains("WHERE \"email\" IS NOT NULL", sql);
    }

    [Fact]
    public async Task GenerateScript_PartialIndex_WithComparisonPredicate()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL,
    status      varchar(20) NOT NULL
);

CREATE INDEX idx_active_orders ON orders (customer_id) WHERE status = 'active';
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CREATE INDEX \"idx_active_orders\" ON \"orders\"", sql);
        Assert.Contains("WHERE \"status\" = 'active'", sql);
    }

    [Fact]
    public async Task GenerateScript_FullIndex_HasNoWhereClause()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);

CREATE INDEX idx_film_title ON film (title);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.DoesNotContain("WHERE", sql);
    }

    [Fact]
    public async Task GenerateScript_IdentityColumns()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE widgets
(
    id        integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
    serial_no integer GENERATED ALWAYS AS IDENTITY
);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        Assert.Contains("\"id\" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY", sql);
        Assert.Contains("\"serial_no\" integer GENERATED ALWAYS AS IDENTITY", sql);
        // Identity columns must not carry an explicit NULL/NOT NULL suffix.
        Assert.DoesNotContain("GENERATED ALWAYS AS IDENTITY NULL", sql);
        Assert.DoesNotContain("GENERATED ALWAYS AS IDENTITY NOT NULL", sql);
    }

    [Fact]
    public async Task GenerateScript_IdentitySequenceOptions_EmitsOptionList()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE widgets
(
    id integer GENERATED BY DEFAULT AS IDENTITY (START WITH 100 INCREMENT BY 5 MINVALUE 100 MAXVALUE 9999 CACHE 10 CYCLE) PRIMARY KEY
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains(
            "\"id\" integer GENERATED BY DEFAULT AS IDENTITY "
            + "(START WITH 100 INCREMENT BY 5 MINVALUE 100 MAXVALUE 9999 CACHE 10 CYCLE) PRIMARY KEY",
            sql);
    }

    [Fact]
    public async Task GenerateScript_IdentityWithDefaultOptions_EmitsNoOptionList()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE widgets
(
    id integer GENERATED BY DEFAULT AS IDENTITY (START WITH 1) PRIMARY KEY
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // START WITH 1 is the default, so nothing is modeled and no option list is emitted.
        Assert.Contains("\"id\" integer GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY", sql);
        Assert.DoesNotContain("START WITH", sql);
    }

    [Fact]
    public async Task GenerateScript_MultiColumnPrimaryKey_EmitsTableLevelClause()
    {
        // The parser now understands table-level PRIMARY KEY (a, b) (issue #7), so the
        // model is built straight from SQL like the other cases.
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE enrollment
(
    student_id integer NOT NULL,
    course_id  integer NOT NULL,
    PRIMARY KEY (student_id, course_id)
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // Multi-column PK must be a table-level clause, not inline on a single column.
        Assert.DoesNotContain("integer PRIMARY KEY", sql);
        // An unnamed table-level PK gets the Postgres <table>_pkey constraint name.
        Assert.Contains("CONSTRAINT \"enrollment_pkey\" PRIMARY KEY (\"student_id\", \"course_id\")", sql);
    }

    [Fact]
    public async Task GenerateScript_NamedMultiColumnPrimaryKey_EmitsConstraintName()
    {
        // A table-level PRIMARY KEY named with CONSTRAINT must keep that name when
        // scripted, so the constraint lands in the database with the intended name.
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE enrollment
(
    student_id integer NOT NULL,
    course_id  integer NOT NULL,
    CONSTRAINT pk_enrollment PRIMARY KEY (student_id, course_id)
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CONSTRAINT \"pk_enrollment\" PRIMARY KEY (\"student_id\", \"course_id\")", sql);
    }

    [Fact]
    public async Task GenerateScript_NamedSingleColumnPrimaryKey_EmitsConstraintName()
    {
        // A single-column PK named with CONSTRAINT must keep that name. It is emitted as a
        // table-level clause (CONSTRAINT name PRIMARY KEY (col)) since an inline PRIMARY KEY
        // has no place for a name.
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer CONSTRAINT pk_film PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CONSTRAINT \"pk_film\" PRIMARY KEY (\"film_id\")", sql);
        // The name must not be silently replaced by the Postgres-generated <table>_pkey.
        Assert.DoesNotContain("film_pkey", sql);
        // And the inline column must not carry a bare PRIMARY KEY (which would be unnamed).
        Assert.DoesNotContain("integer PRIMARY KEY", sql);
    }

    [Fact]
    public async Task GenerateScript_UnnamedSingleColumnPrimaryKey_StaysInline()
    {
        // An unnamed single-column PK keeps the clean inline form (no redundant
        // table-level CONSTRAINT <table>_pkey clause).
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE film
(
    film_id integer PRIMARY KEY,
    title   varchar(255) NOT NULL
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("\"film_id\" integer PRIMARY KEY", sql);
        Assert.DoesNotContain("CONSTRAINT", sql);
    }

    [Fact]
    public async Task GenerateScript_UniqueIndexWithMethodAndDirection()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE account
(
    account_id integer PRIMARY KEY,
    email      varchar(255) NOT NULL
);

CREATE UNIQUE INDEX idx_account_email ON account USING btree (email DESC NULLS LAST);
""");

        var generator = new PostgresScriptGenerator();

        var sql = generator.GenerateScript(comparison);

        // btree is Postgres's default access method, so "USING btree" is suppressed to
        // keep the DDL clean (the model still records btree so parsed and extracted models
        // hash-match). DESC defaults to NULLS FIRST, so the explicit NULLS LAST is emitted.
        Assert.Contains("CREATE UNIQUE INDEX \"idx_account_email\" ON \"account\" (", sql);
        Assert.DoesNotContain("USING btree", sql);
        Assert.Contains("\"email\" DESC NULLS LAST", sql);
    }

    [Fact]
    public async Task GenerateScript_InlineForeignKeyWithOnDeleteCascade()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE customers
(
    id integer PRIMARY KEY
);

CREATE TABLE orders
(
    id          integer PRIMARY KEY,
    customer_id integer NOT NULL REFERENCES customers (id) ON DELETE CASCADE
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains(
            "CONSTRAINT \"orders_customer_id_fkey\" FOREIGN KEY (\"customer_id\") REFERENCES \"customers\" (\"id\") ON DELETE CASCADE",
            sql);
    }

    [Fact]
    public async Task GenerateScript_TableLevelCompositeForeignKey()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE orders
(
    id      integer NOT NULL,
    line_no integer NOT NULL,
    PRIMARY KEY (id, line_no)
);

CREATE TABLE order_lines
(
    order_id integer NOT NULL,
    line_no  integer NOT NULL,
    CONSTRAINT fk_lines FOREIGN KEY (order_id, line_no) REFERENCES orders (id, line_no) ON DELETE CASCADE ON UPDATE RESTRICT
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains(
            "CONSTRAINT \"fk_lines\" FOREIGN KEY (\"order_id\", \"line_no\") REFERENCES \"orders\" (\"id\", \"line_no\") ON DELETE CASCADE ON UPDATE RESTRICT",
            sql);
    }

    [Fact]
    public async Task GenerateScript_CreateExtension()
    {
        var comparison = await CompareToEmptyAsync("CREATE EXTENSION citext;");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // IF NOT EXISTS keeps publish idempotent for an already-installed extension.
        Assert.Contains("CREATE EXTENSION IF NOT EXISTS \"citext\";", sql);
    }

    [Fact]
    public async Task GenerateScript_CreateExtensionWithVersion()
    {
        var comparison = await CompareToEmptyAsync("CREATE EXTENSION citext WITH VERSION '1.6';");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("CREATE EXTENSION IF NOT EXISTS \"citext\" VERSION '1.6';", sql);
    }

    [Fact]
    public async Task GenerateScript_VectorColumn_ScriptsWithDimension()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE items
(
    id        integer PRIMARY KEY,
    embedding vector(3)
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // The dimension modifier must be preserved in the emitted column definition.
        Assert.Contains("\"embedding\" vector(3)", sql);
    }

    [Fact]
    public async Task GenerateScript_ColumnWithIntegerDefault()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE widgets
(
    id    integer PRIMARY KEY,
    count integer NOT NULL DEFAULT 0
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("\"count\" integer NOT NULL DEFAULT 0", sql);
    }

    [Fact]
    public async Task GenerateScript_ColumnWithStringDefault()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE orders
(
    id     integer PRIMARY KEY,
    status varchar(20) NOT NULL DEFAULT 'active'
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("\"status\" varchar(20) NOT NULL DEFAULT 'active'", sql);
    }

    [Fact]
    public async Task GenerateScript_ColumnWithBooleanDefault()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE flags
(
    id       integer PRIMARY KEY,
    enabled  boolean NOT NULL DEFAULT true
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("\"enabled\" boolean NOT NULL DEFAULT true", sql);
    }

    [Fact]
    public async Task GenerateScript_ColumnWithNumericDefault_PreservesScale()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE prices
(
    id      integer PRIMARY KEY,
    balance numeric(8, 2) NOT NULL DEFAULT 1.50
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // The literal scale must be preserved (1.50, not 1.5) so it matches how Postgres
        // stores the numeric default and the parsed model hashes to the extracted one.
        Assert.Contains("\"balance\" numeric(8, 2) NOT NULL DEFAULT 1.50", sql);
    }

    [Fact]
    public async Task GenerateScript_FunctionDefault_IsNotModeled()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE events
(
    id         integer PRIMARY KEY,
    created_at timestamp NOT NULL DEFAULT now()
);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        // A function default is out of scope: it must not be emitted (and must not crash).
        Assert.DoesNotContain("DEFAULT", sql);
    }

    [Fact]
    public async Task GenerateScript_HnswIndex_EmitsOperatorClassAndStorageParameters()
    {
        var comparison = await CompareToEmptyAsync("""
CREATE TABLE items
(
    id        integer PRIMARY KEY,
    embedding vector(3)
);

CREATE INDEX items_embedding_idx ON items USING hnsw (embedding vector_cosine_ops) WITH (m = 16, ef_construction = 64);
""");

        var sql = new PostgresScriptGenerator().GenerateScript(comparison);

        Assert.Contains("USING hnsw", sql);
        Assert.Contains("(\"embedding\" vector_cosine_ops)", sql);
        Assert.Contains("WITH (m=16, ef_construction=64)", sql);
    }
}
