using System.Reflection;

namespace Squill.Provider.Postgres.Tests;

/// <summary>
/// Unit coverage for the two halves of the <c>DEFAULT</c> round trip meeting in the middle
/// (issue #124): the canonical token produced from a parsed expression must equal the one
/// produced from the text Postgres stores in <c>information_schema.columns.column_default</c>.
///
/// The database-side inputs here are the literal strings a real Postgres reports — verified
/// against a live server — so this test pins the contract that the integration test then
/// proves end to end.
/// </summary>
public class PostgresDefaultValueCanonicalTests
{
    // PostgresDefaultValue is internal; reach it reflectively rather than widening its
    // accessibility just for a test.
    private static readonly MethodInfo FromDatabaseTextMethod =
        typeof(PostgresElementTypes).Assembly
            .GetType("Squill.Provider.Postgres.PostgresDefaultValue", throwOnError: true)!
            .GetMethod("FromDatabaseText", BindingFlags.Public | BindingFlags.Static)!;

    private static string? FromDatabaseText(string? text)
        => (string?)FromDatabaseTextMethod.Invoke(null, [text]);

    [Theory]
    // Exactly as Postgres reports them for DEFAULT now() / NOW() / pg_catalog.now().
    [InlineData("now()", "now()")]
    [InlineData("gen_random_uuid()", "gen_random_uuid()")]
    // Postgres preserves the CURRENT_TIMESTAMP spelling in upper case.
    [InlineData("CURRENT_TIMESTAMP", "CURRENT_TIMESTAMP")]
    [InlineData("CURRENT_DATE", "CURRENT_DATE")]
    public void DatabaseText_ForSupportedFunction_Canonicalizes(string stored, string expected)
    {
        Assert.Equal(expected, FromDatabaseText(stored));
    }

    [Theory]
    // A serial column's default — must stay unmodeled, the sequence name is generated.
    [InlineData("nextval('t_id_seq'::regclass)")]
    // Not on the allowlist: could be rewritten by Postgres, so cannot be trusted to round-trip.
    [InlineData("some_custom_fn(1)")]
    [InlineData("(a + b)")]
    public void DatabaseText_ForUnsupportedExpression_IsNotModeled(string stored)
    {
        Assert.Null(FromDatabaseText(stored));
    }

    [Theory]
    // The pre-existing constant-literal behavior must be unchanged by issue #124.
    [InlineData("0", "0")]
    [InlineData("'-5'::integer", "-5")]
    [InlineData("'active'::character varying", "'active'")]
    [InlineData("true", "true")]
    public void DatabaseText_ForConstantLiteral_IsUnchanged(string stored, string expected)
    {
        Assert.Equal(expected, FromDatabaseText(stored));
    }
}
