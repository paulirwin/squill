using Squill.MariaDbParser.Syntax;
using Squill.TestFramework;

namespace Squill.MariaDbParser.Tests;

/// <summary>
/// Parser-level tests for CREATE TABLE columns and constraints, asserting the syntax tree the
/// mapper produces (issue #123). Column data types have their own file
/// (<see cref="CreateTableDataTypeTests"/>); model-level concerns — element shape, synthesized
/// constraint names such as <c>PRIMARY</c> and <c>&lt;table&gt;_ibfk_N</c>, script generation —
/// are covered in Squill.Provider.MariaDb.Tests.
/// </summary>
public class CreateTableTests
{
    private static CreateTableStatement ParseOne(string text)
        => ParseAssertions.Single<CreateTableStatement>(new AntlrMariaDbParser().Parse(text).Statements);

    private static ColumnDefinition Column(CreateTableStatement table, string columnName)
        => table.Elements.OfType<ColumnDefinition>().Single(c => c.Name.Name == columnName);

    /// <summary>
    /// The constraints of a column, unwrapping any <see cref="NamedColumnConstraint"/> so a
    /// test can assert the constraint kind regardless of whether it was CONSTRAINT-named.
    /// </summary>
    private static IEnumerable<ColumnConstraint> Unwrapped(ColumnDefinition column)
        => column.Constraints.Select(c => c is NamedColumnConstraint named ? named.Constraint : c);

    /// <summary>
    /// The single table-level constraint of type <typeparamref name="T"/>, unwrapping a
    /// <see cref="NamedTableConstraint"/> and returning the name that wrapper carried (null
    /// when the constraint was written without a CONSTRAINT clause).
    /// </summary>
    private static (T Constraint, string? Name) TableConstraint<T>(CreateTableStatement table)
        where T : Syntax.TableConstraint
    {
        var match = table.Elements
            .OfType<Syntax.TableConstraint>()
            .Select(c => c is NamedTableConstraint named
                ? (Constraint: named.Constraint, Name: named.Name)
                : (Constraint: c, Name: (string?)null))
            .Single(c => c.Constraint is T);

        return ((T)match.Constraint, match.Name);
    }

    // ---- Table name and columns ----

    [Fact]
    public void CreateTable_CapturesNameAndColumnsInOrder()
    {
        var table = ParseOne(
            """
            CREATE TABLE actor (
                actor_id int,
                first_name varchar(45),
                last_name varchar(45)
            );
            """);

        Assert.Equal("actor", table.Name.Name);
        Assert.Equal(
            new[] { "actor_id", "first_name", "last_name" },
            table.Elements.OfType<ColumnDefinition>().Select(c => c.Name.Name));
    }

    // A database-qualified name keeps both segments, so the model builder can tell an
    // explicitly-qualified table from a bare one.
    [Fact]
    public void CreateTable_QualifiedName_CapturesBothSegments()
    {
        var table = ParseOne("CREATE TABLE sakila.actor (actor_id int);");

        Assert.Equal(new[] { "sakila", "actor" }, table.Name.Segments.Select(s => s.Name));
        Assert.Equal("actor", table.Name.Name);
        Assert.Equal("sakila.actor", table.Name.ToString());
    }

    // Backtick quoting is MariaDB's identifier delimiter; the quotes are stripped, and a
    // quoted identifier may contain characters a bare one could not.
    [Fact]
    public void CreateTable_BacktickIdentifiers_AreUnquoted()
    {
        var table = ParseOne("CREATE TABLE `order` (`select` int, `two words` int);");

        Assert.Equal("order", table.Name.Name);
        Assert.Equal(
            new[] { "select", "two words" },
            table.Elements.OfType<ColumnDefinition>().Select(c => c.Name.Name));
    }

    // ---- Nullability, AUTO_INCREMENT and DEFAULT ----

    // NOT NULL and an explicit NULL are distinct constraints; a column with neither carries no
    // nullability constraint at all, leaving the default (nullable) to the model builder.
    [Fact]
    public void Column_NotNull_CapturesNonNullable()
    {
        var column = Column(ParseOne("CREATE TABLE t (c int NOT NULL);"), "c");

        var nullable = Assert.Single(Unwrapped(column).OfType<NullableColumnConstraint>());
        Assert.False(nullable.Nullable);
    }

    [Fact]
    public void Column_ExplicitNull_CapturesNullable()
    {
        var column = Column(ParseOne("CREATE TABLE t (c int NULL);"), "c");

        var nullable = Assert.Single(Unwrapped(column).OfType<NullableColumnConstraint>());
        Assert.True(nullable.Nullable);
    }

    [Fact]
    public void Column_NoNullabilityWritten_HasNoNullableConstraint()
    {
        var column = Column(ParseOne("CREATE TABLE t (c int);"), "c");

        Assert.Empty(Unwrapped(column).OfType<NullableColumnConstraint>());
    }

    [Fact]
    public void Column_AutoIncrement_CapturesConstraint()
    {
        var column = Column(
            ParseOne("CREATE TABLE t (id int NOT NULL AUTO_INCREMENT PRIMARY KEY);"),
            "id");

        Assert.Single(Unwrapped(column).OfType<AutoIncrementColumnConstraint>());
        Assert.Single(Unwrapped(column).OfType<PrimaryKeyColumnConstraint>());
    }

    // The DEFAULT token is kept exactly as written — quotes included for a string literal —
    // because the provider scripts it back out verbatim rather than re-rendering it.
    [Theory]
    [InlineData("int DEFAULT 5", "5")]
    [InlineData("varchar(10) DEFAULT 'active'", "'active'")]
    [InlineData("int DEFAULT NULL", "NULL")]
    [InlineData("timestamp DEFAULT CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP")]
    public void Column_Default_CapturesRawToken(string declared, string expectedToken)
    {
        var column = Column(ParseOne($"CREATE TABLE t (c {declared});"), "c");

        var @default = Assert.Single(Unwrapped(column).OfType<DefaultColumnConstraint>());
        Assert.Equal(expectedToken, @default.Token);
    }

    // Constraints are recorded in the order written, so the provider sees the same sequence
    // the author declared.
    [Fact]
    public void Column_MultipleConstraints_AreCapturedInOrder()
    {
        var column = Column(
            ParseOne("CREATE TABLE t (c int NOT NULL DEFAULT 0 UNIQUE);"),
            "c");

        Assert.Collection(
            Unwrapped(column),
            c => Assert.False(Assert.IsType<NullableColumnConstraint>(c).Nullable),
            c => Assert.Equal("0", Assert.IsType<DefaultColumnConstraint>(c).Token),
            c => Assert.IsType<UniqueKeyColumnConstraint>(c));
    }

    // COMMENT/COLLATE are recognized but not modeled — they must not derail parsing of the
    // constraints around them.
    [Fact]
    public void Column_UnmodeledConstraint_IsIgnoredButDoesNotBreakParsing()
    {
        var column = Column(
            ParseOne("CREATE TABLE t (c int NOT NULL COMMENT 'a note');"),
            "c");

        Assert.Single(Unwrapped(column).OfType<NullableColumnConstraint>());
        Assert.Single(Unwrapped(column).OfType<IgnoredColumnConstraint>());
    }

    // ---- Inline (column-level) PRIMARY KEY / UNIQUE ----

    [Fact]
    public void Column_InlinePrimaryKey_CapturesConstraint()
    {
        var column = Column(ParseOne("CREATE TABLE t (id int PRIMARY KEY);"), "id");

        Assert.Single(Unwrapped(column).OfType<PrimaryKeyColumnConstraint>());
    }

    [Fact]
    public void Column_InlineUnique_CapturesConstraint()
    {
        var column = Column(ParseOne("CREATE TABLE t (email varchar(255) UNIQUE);"), "email");

        Assert.Single(Unwrapped(column).OfType<UniqueKeyColumnConstraint>());
    }

    // ---- Table-level PRIMARY KEY ----

    [Fact]
    public void TablePrimaryKey_CapturesColumn()
    {
        var table = ParseOne("CREATE TABLE t (id int, PRIMARY KEY (id));");

        var (pk, name) = TableConstraint<PrimaryKeyTableConstraint>(table);

        Assert.Null(name);
        Assert.Equal(new[] { "id" }, pk.Columns.Select(c => c.Column?.Name));
    }

    // A composite PK keeps its columns in the order written; that order is the index order.
    [Fact]
    public void TablePrimaryKey_Composite_CapturesColumnsInOrder()
    {
        var table = ParseOne(
            "CREATE TABLE film_actor (actor_id int, film_id int, PRIMARY KEY (actor_id, film_id));");

        var (pk, _) = TableConstraint<PrimaryKeyTableConstraint>(table);

        Assert.Equal(new[] { "actor_id", "film_id" }, pk.Columns.Select(c => c.Column?.Name));
    }

    // MariaDB always names the primary key `PRIMARY` regardless of what CONSTRAINT clause is
    // written, but the parser records the written name faithfully and leaves that
    // normalization to the model builder.
    [Fact]
    public void TablePrimaryKey_WithConstraintName_CapturesWrittenName()
    {
        var table = ParseOne("CREATE TABLE t (id int, CONSTRAINT pk_t PRIMARY KEY (id));");

        var (pk, name) = TableConstraint<PrimaryKeyTableConstraint>(table);

        Assert.Equal("pk_t", name);
        Assert.Equal(new[] { "id" }, pk.Columns.Select(c => c.Column?.Name));
    }

    // ---- Table-level UNIQUE ----

    [Fact]
    public void TableUnique_CapturesColumns()
    {
        var table = ParseOne("CREATE TABLE t (a int, b int, UNIQUE KEY (a, b));");

        var (unique, name) = TableConstraint<UniqueKeyTableConstraint>(table);

        Assert.Null(name);
        Assert.Null(unique.IndexName);
        Assert.Equal(new[] { "a", "b" }, unique.Columns.Select(c => c.Column?.Name));
    }

    // `UNIQUE KEY <index-name> (...)` names the backing index; that trailing uid is the index
    // name, not a constraint name.
    [Fact]
    public void TableUnique_WithIndexName_CapturesIndexName()
    {
        var table = ParseOne("CREATE TABLE t (email varchar(255), UNIQUE KEY idx_email (email));");

        var (unique, name) = TableConstraint<UniqueKeyTableConstraint>(table);

        Assert.Null(name);
        Assert.Equal("idx_email", unique.IndexName);
        Assert.Equal(new[] { "email" }, unique.Columns.Select(c => c.Column?.Name));
    }

    // With both a CONSTRAINT name and an index name the leading uid is the constraint name and
    // the trailing one the index name — the case IndexNameFromUids exists to disambiguate.
    [Fact]
    public void TableUnique_WithConstraintAndIndexName_CapturesBoth()
    {
        var table = ParseOne(
            "CREATE TABLE t (email varchar(255), CONSTRAINT uq_email UNIQUE KEY idx_email (email));");

        var (unique, name) = TableConstraint<UniqueKeyTableConstraint>(table);

        Assert.Equal("uq_email", name);
        Assert.Equal("idx_email", unique.IndexName);
    }

    // ---- Table-level FOREIGN KEY ----

    [Fact]
    public void TableForeignKey_CapturesColumnsAndReference()
    {
        var table = ParseOne(
            """
            CREATE TABLE film_actor (
                actor_id int,
                FOREIGN KEY (actor_id) REFERENCES actor (actor_id)
            );
            """);

        var (fk, name) = TableConstraint<ForeignKeyTableConstraint>(table);

        Assert.Null(name);
        Assert.Equal(new[] { "actor_id" }, fk.Columns.Select(c => c.Name));
        Assert.Equal("actor", fk.ReferencedTable.Name);
        Assert.Equal(new[] { "actor_id" }, fk.ReferencedColumns.Select(c => c.Name));
        Assert.Null(fk.OnDelete);
        Assert.Null(fk.OnUpdate);
    }

    [Fact]
    public void TableForeignKey_WithConstraintName_CapturesWrittenName()
    {
        var table = ParseOne(
            """
            CREATE TABLE film_actor (
                actor_id int,
                CONSTRAINT fk_film_actor_actor FOREIGN KEY (actor_id) REFERENCES actor (actor_id)
            );
            """);

        var (_, name) = TableConstraint<ForeignKeyTableConstraint>(table);

        Assert.Equal("fk_film_actor_actor", name);
    }

    [Fact]
    public void TableForeignKey_Composite_CapturesColumnsInOrder()
    {
        var table = ParseOne(
            """
            CREATE TABLE t (
                a int,
                b int,
                FOREIGN KEY (a, b) REFERENCES parent (x, y)
            );
            """);

        var (fk, _) = TableConstraint<ForeignKeyTableConstraint>(table);

        Assert.Equal(new[] { "a", "b" }, fk.Columns.Select(c => c.Name));
        Assert.Equal(new[] { "x", "y" }, fk.ReferencedColumns.Select(c => c.Name));
    }

    // Referential actions are mapped onto the right clause regardless of the order the two
    // ON clauses are written in. MariaDB treats NO ACTION as RESTRICT.
    [Theory]
    [InlineData("ON DELETE CASCADE", ReferentialAction.Cascade, null)]
    [InlineData("ON UPDATE CASCADE", null, ReferentialAction.Cascade)]
    [InlineData("ON DELETE SET NULL", ReferentialAction.SetNull, null)]
    [InlineData("ON DELETE RESTRICT", ReferentialAction.Restrict, null)]
    [InlineData("ON DELETE NO ACTION", ReferentialAction.Restrict, null)]
    [InlineData("ON DELETE CASCADE ON UPDATE RESTRICT", ReferentialAction.Cascade, ReferentialAction.Restrict)]
    [InlineData("ON UPDATE CASCADE ON DELETE SET NULL", ReferentialAction.SetNull, ReferentialAction.Cascade)]
    public void TableForeignKey_CapturesReferentialActions(
        string actions,
        ReferentialAction? expectedOnDelete,
        ReferentialAction? expectedOnUpdate)
    {
        var table = ParseOne(
            $"CREATE TABLE t (a int, FOREIGN KEY (a) REFERENCES parent (x) {actions});");

        var (fk, _) = TableConstraint<ForeignKeyTableConstraint>(table);

        Assert.Equal(expectedOnDelete, fk.OnDelete);
        Assert.Equal(expectedOnUpdate, fk.OnUpdate);
    }

    // ---- Inline (column-level) REFERENCES ----

    [Fact]
    public void Column_InlineReferences_CapturesReference()
    {
        var column = Column(
            ParseOne("CREATE TABLE t (actor_id int REFERENCES actor (actor_id));"),
            "actor_id");

        var fk = Assert.Single(Unwrapped(column).OfType<ForeignKeyColumnConstraint>());

        Assert.Equal("actor", fk.ReferencedTable.Name);
        Assert.Equal("actor_id", fk.ReferencedColumn?.Name);
    }

    [Fact]
    public void Column_InlineReferences_CapturesReferentialActions()
    {
        var column = Column(
            ParseOne(
                "CREATE TABLE t (a int REFERENCES parent (x) ON DELETE CASCADE ON UPDATE RESTRICT);"),
            "a");

        var fk = Assert.Single(Unwrapped(column).OfType<ForeignKeyColumnConstraint>());

        Assert.Equal(ReferentialAction.Cascade, fk.OnDelete);
        Assert.Equal(ReferentialAction.Restrict, fk.OnUpdate);
    }

    // ---- Inline INDEX/KEY declarations ----

    [Fact]
    public void TableIndex_CapturesNameAndColumns()
    {
        var table = ParseOne("CREATE TABLE t (a int, b int, INDEX idx_a_b (a, b));");

        var index = Assert.Single(table.Elements.OfType<IndexTableConstraint>());

        Assert.Equal("idx_a_b", index.IndexName);
        Assert.Null(index.IndexMethod);
        Assert.Equal(new[] { "a", "b" }, index.Columns.Select(c => c.Column?.Name));
    }

    // `KEY` is a synonym for `INDEX` in a CREATE TABLE body.
    [Fact]
    public void TableIndex_KeySynonym_IsCaptured()
    {
        var table = ParseOne("CREATE TABLE t (a int, KEY idx_a (a));");

        var index = Assert.Single(table.Elements.OfType<IndexTableConstraint>());

        Assert.Equal("idx_a", index.IndexName);
        Assert.Equal(new[] { "a" }, index.Columns.Select(c => c.Column?.Name));
    }

    // USING may be written either before the column list or after it; MariaDB accepts both
    // and the grammar binds them to different rules, so both must reach IndexMethod.
    [Theory]
    [InlineData("INDEX idx_a USING BTREE (a)", "BTREE")]
    [InlineData("INDEX idx_a (a) USING BTREE", "BTREE")]
    [InlineData("INDEX idx_a USING HASH (a)", "HASH")]
    [InlineData("INDEX idx_a (a) USING HASH", "HASH")]
    public void TableIndex_WithMethod_CapturesMethod(string declaration, string expectedMethod)
    {
        var table = ParseOne($"CREATE TABLE t (a int, {declaration});");

        var index = Assert.Single(table.Elements.OfType<IndexTableConstraint>());

        Assert.Equal(expectedMethod, index.IndexMethod);
        Assert.Equal(new[] { "a" }, index.Columns.Select(c => c.Column?.Name));
    }

    // FULLTEXT and SPATIAL indexes are carried as an ordinary index constraint tagged with the
    // kind (issue #146). The kind is kept apart from the index method because it is not one:
    // both engines reject `USING FULLTEXT`, so it is written as a leading keyword instead.
    [Theory]
    [InlineData("FULLTEXT KEY ft_a (a)", "FULLTEXT", "ft_a")]
    [InlineData("FULLTEXT INDEX ft_a (a)", "FULLTEXT", "ft_a")]
    [InlineData("SPATIAL KEY sp_a (a)", "SPATIAL", "sp_a")]
    [InlineData("SPATIAL INDEX sp_a (a)", "SPATIAL", "sp_a")]
    public void TableIndex_SpecialKind_IsCarriedWithItsKind(
        string declaration, string expectedKind, string expectedName)
    {
        var table = ParseOne($"CREATE TABLE t (a text, {declaration});");

        Assert.Empty(table.Elements.OfType<IgnoredTableConstraint>());

        var index = Assert.Single(table.Elements.OfType<IndexTableConstraint>());
        Assert.Equal(expectedKind, index.IndexKind);
        Assert.Equal(expectedName, index.IndexName);
        Assert.Null(index.IndexMethod);
        Assert.Equal(new[] { "a" }, index.Columns.Select(c => c.Column?.Name));
    }

    // An ordinary index carries no kind, so the two forms stay distinguishable.
    [Fact]
    public void TableIndex_OrdinaryKind_HasNoIndexKind()
    {
        var table = ParseOne("CREATE TABLE t (a text, KEY k_a (a));");

        Assert.Null(Assert.Single(table.Elements.OfType<IndexTableConstraint>()).IndexKind);
    }

    // ---- Source positions ----

    // Table constraints carry the 1-based line/column they start at, so build diagnostics can
    // point back into the source file (issue #53).
    [Fact]
    public void TableConstraint_RecordsSourcePosition()
    {
        var table = ParseOne(
            """
            CREATE TABLE t (
                id int,
                PRIMARY KEY (id)
            );
            """);

        var (pk, _) = TableConstraint<PrimaryKeyTableConstraint>(table);

        Assert.Equal(3, pk.Line);
        Assert.Equal(5, pk.Column);
    }

    // ---- Whole-table smoke test ----

    // A realistic table exercising columns, inline and table-level constraints together, to
    // catch interactions the focused tests above would each miss.
    [Fact]
    public void CreateTable_FullTable_CapturesAllElements()
    {
        var table = ParseOne(
            """
            CREATE TABLE rental (
                rental_id int NOT NULL AUTO_INCREMENT,
                rental_date datetime NOT NULL,
                inventory_id mediumint unsigned NOT NULL,
                customer_id smallint unsigned NOT NULL,
                return_date datetime DEFAULT NULL,
                last_update timestamp NOT NULL DEFAULT CURRENT_TIMESTAMP,
                PRIMARY KEY (rental_id),
                UNIQUE KEY idx_uq (rental_date, inventory_id, customer_id),
                KEY idx_fk_customer_id (customer_id),
                CONSTRAINT fk_rental_customer FOREIGN KEY (customer_id)
                    REFERENCES customer (customer_id) ON DELETE RESTRICT ON UPDATE CASCADE
            );
            """);

        Assert.Equal(
            new[]
            {
                "rental_id", "rental_date", "inventory_id",
                "customer_id", "return_date", "last_update",
            },
            table.Elements.OfType<ColumnDefinition>().Select(c => c.Name.Name));

        var (pk, _) = TableConstraint<PrimaryKeyTableConstraint>(table);
        Assert.Equal(new[] { "rental_id" }, pk.Columns.Select(c => c.Column?.Name));

        var (unique, _) = TableConstraint<UniqueKeyTableConstraint>(table);
        Assert.Equal("idx_uq", unique.IndexName);
        Assert.Equal(
            new[] { "rental_date", "inventory_id", "customer_id" },
            unique.Columns.Select(c => c.Column?.Name));

        var index = Assert.Single(table.Elements.OfType<IndexTableConstraint>());
        Assert.Equal("idx_fk_customer_id", index.IndexName);

        var (fk, fkName) = TableConstraint<ForeignKeyTableConstraint>(table);
        Assert.Equal("fk_rental_customer", fkName);
        Assert.Equal("customer", fk.ReferencedTable.Name);
        Assert.Equal(ReferentialAction.Restrict, fk.OnDelete);
        Assert.Equal(ReferentialAction.Cascade, fk.OnUpdate);

        Assert.Single(
            Unwrapped(Column(table, "rental_id")).OfType<AutoIncrementColumnConstraint>());
    }

    /// <summary>
    /// TEMPORARY is carried on the statement so the provider can reject it against the
    /// statement's position (issue #204). Before that it was parsed and then dropped, so a
    /// temporary table deployed as a permanent one.
    /// </summary>
    [Theory]
    [InlineData("CREATE TEMPORARY TABLE scratch (id int);")]
    [InlineData("CREATE temporary TABLE scratch (id int);")]
    public void CreateTable_Temporary_IsCarried(string sql)
    {
        var table = ParseOne(sql);

        Assert.True(table.IsTemporary);

        // The rest of the statement still parses normally.
        Assert.Equal("scratch", table.Name.Name);
        Assert.Single(table.Elements.OfType<ColumnDefinition>());
    }

    [Fact]
    public void CreateTable_Ordinary_IsNotTemporary()
    {
        Assert.False(ParseOne("CREATE TABLE keeper (id int PRIMARY KEY);").IsTemporary);
    }
}
