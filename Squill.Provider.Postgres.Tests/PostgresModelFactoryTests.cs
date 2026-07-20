using Squill.Core;

namespace Squill.Provider.Postgres.Tests;

public class PostgresModelFactoryTests
{
    [Fact]
    public void CreateIndex_ShapeAndProperties()
    {
        var index = PostgresModelFactory.CreateIndex(
            SqlName.Object("idx_film_title"),
            SqlName.Object("film"),
            isUnique: false,
            indexMethod: null,
            columns: [new PostgresModelFactory.IndexedColumn(SqlName.Object("film").Child("title"))]);

        Assert.Equal(PostgresElementTypes.SqlIndex, index.Type);
        Assert.Equal("\"idx_film_title\"", index.Name);
        Assert.Equal(false, index.GetProperty<bool?>(PostgresPropertyNames.IsUnique));
        Assert.Null(index.GetProperty<string>(PostgresPropertyNames.IndexMethod));

        // Relationship order matters for hashing: ColumnSpecifications then IndexedObject.
        Assert.Equal(PostgresRelationshipNames.ColumnSpecifications, index.Relationships[0].Name);
        Assert.Equal(PostgresRelationshipNames.IndexedObject, index.Relationships[1].Name);

        var indexedObject = (Reference)index.Relationships[1].Entries.Single();
        Assert.Equal("\"film\"", indexedObject.Name);
    }

    [Fact]
    public void CreateIndex_WithMethodAndDirection()
    {
        var index = PostgresModelFactory.CreateIndex(
            SqlName.Object("idx_account_email"),
            SqlName.Object("account"),
            isUnique: true,
            indexMethod: "btree",
            columns:
            [
                new PostgresModelFactory.IndexedColumn(
                    SqlName.Object("account").Child("email"), IsAscending: false, NullsFirst: false)
            ]);

        Assert.Equal(true, index.GetProperty<bool?>(PostgresPropertyNames.IsUnique));
        Assert.Equal("btree", index.GetProperty<string>(PostgresPropertyNames.IndexMethod));

        var spec = (Element)index.Relationships[0].Entries.Single();
        Assert.Equal(false, spec.GetProperty<bool?>(PostgresPropertyNames.IsAscending));
        Assert.Equal(false, spec.GetProperty<bool?>(PostgresPropertyNames.NullsFirst));
    }

    [Fact]
    public void CreatePrimaryKey_Shape()
    {
        var pk = PostgresModelFactory.CreatePrimaryKey(
            SqlName.Object("PK_film"),
            SqlName.Object("film"),
            columns: [new PostgresModelFactory.IndexedColumn(SqlName.Object("film").Child("film_id"))]);

        Assert.Equal(PostgresElementTypes.SqlPrimaryKeyConstraint, pk.Type);
        Assert.Equal("\"PK_film\"", pk.Name);
        Assert.Equal(PostgresRelationshipNames.ColumnSpecifications, pk.Relationships[0].Name);
        Assert.Equal(PostgresRelationshipNames.DefiningTable, pk.Relationships[1].Name);

        var definingTable = (Reference)pk.Relationships[1].Entries.Single();
        Assert.Equal("\"film\"", definingTable.Name);
    }

    [Fact]
    public void CreatePrimaryKey_MultipleColumns()
    {
        var pk = PostgresModelFactory.CreatePrimaryKey(
            SqlName.Object("PK_enrollment"),
            SqlName.Object("enrollment"),
            columns:
            [
                new PostgresModelFactory.IndexedColumn(SqlName.Object("enrollment").Child("student_id")),
                new PostgresModelFactory.IndexedColumn(SqlName.Object("enrollment").Child("course_id")),
            ]);

        var specs = pk.Relationships[0].Entries.OfType<Element>().ToList();
        Assert.Equal(2, specs.Count);
    }

    [Fact]
    public void CreateIndexedColumnSpecification_OmitsUnspecifiedOrdering()
    {
        var spec = PostgresModelFactory.CreateIndexedColumnSpecification(
            new PostgresModelFactory.IndexedColumn(SqlName.Object("film").Child("title")));

        Assert.Empty(spec.Properties);
        var reference = (Reference)spec.Relationships.Single().Entries.Single();
        Assert.Equal("\"film\".\"title\"", reference.Name);
    }
}
