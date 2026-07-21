namespace Squill.Provider.MariaDb.Tests;

public class MariaDbSquillProviderTests
{
    [Theory]
    [InlineData("MariaDb")]
    [InlineData("mariadb")]
    [InlineData("MySql")]
    [InlineData("mysql")]
    [InlineData("MYSQL")]
    public void Matches_MariaDbAndMySqlNames(string name)
    {
        Assert.True(new MariaDbSquillProvider().Matches(name));
    }

    [Theory]
    [InlineData("Postgresql")]
    [InlineData("Oracle")]
    [InlineData("")]
    public void Matches_RejectsOtherNames(string name)
    {
        Assert.False(new MariaDbSquillProvider().Matches(name));
    }

    [Fact]
    public void Name_IsMariaDb()
    {
        Assert.Equal("MariaDb", new MariaDbSquillProvider().Name);
    }
}
