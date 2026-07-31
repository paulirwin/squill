using Squill.Core;
using Squill.PostgresParser;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

/// <summary>
/// Reports source constructs introduced in a later PostgreSQL major version than the project
/// targets (issue #142), as SQ1003 warnings.
///
/// <para>
/// This runs at build time because the alternative is finding out mid-deploy. The target
/// version was already recorded and enforced, but only against the server's reported version
/// when the deploy started — so a too-new construct built cleanly, deployed until it reached
/// that statement, and failed there with earlier statements already applied.
/// </para>
///
/// <para>
/// The construct is still modeled. Dropping it would build a model that silently means
/// something other than the source says — for <c>NULLS NOT DISTINCT</c> that would be the
/// opposite uniqueness semantics — which is the failure #141 called out for typed literals.
/// The warning is the whole of the response, and a project that wants it fatal escalates
/// SQ1003 through <c>MSBuildWarningsAsErrors</c>.
/// </para>
/// </summary>
internal static class PostgresTargetVersionChecker
{
    /// <summary>
    /// Adds a diagnostic if <paramref name="statement"/> declares an index using a construct
    /// the target major does not accept.
    /// </summary>
    /// <param name="table">
    /// The unqualified name of the table the index is on, resolved by the caller — an index
    /// name is optional in PostgreSQL (<c>CREATE INDEX ON t (…)</c> lets the server derive one),
    /// so the table is what names an anonymous index in the message.
    /// </param>
    public static void Check(
        IFile file,
        CreateIndexStatement statement,
        string table,
        PostgresqlDatabaseSchemaProvider schemaProvider,
        List<SqlSourceDiagnostic> warnings)
    {
        if (!statement.NullsNotDistinct)
        {
            return;
        }

        var subject = statement.Name is { } name
            ? $"Index '{name.Name}'"
            : $"An index on '{table}'";

        Report(
            file,
            $"{subject} is declared NULLS NOT DISTINCT",
            statement.Line,
            statement.Column,
            PostgresVersionedFeature.NullsNotDistinct,
            schemaProvider,
            warnings);
    }

    /// <summary>
    /// Adds a diagnostic for each non-decimal integer literal (<c>0x19</c>, <c>0o17</c>,
    /// <c>0b101</c>) anywhere inside <paramref name="expression"/>, which the target major does
    /// not accept (issue #191).
    /// </summary>
    /// <param name="subject">
    /// What the expression belongs to, e.g. <c>"The default for column 'mask'"</c>. The literal
    /// itself is quoted into the message on top of this, since a predicate may contain several
    /// and the author needs to know which one is being reported.
    /// </param>
    public static void CheckExpression(
        IFile file,
        Expression expression,
        string subject,
        PostgresqlDatabaseSchemaProvider schemaProvider,
        List<SqlSourceDiagnostic> warnings)
    {
        // Nothing below the introducing version can be at fault, and walking the tree to find
        // that out would be wasted work on by far the common case.
        if (schemaProvider.MajorVersion
            >= PostgresVersionedFeature.NonDecimalIntegerLiteral.MinimumMajorVersion)
        {
            return;
        }

        foreach (var literal in ExpressionWalker.DescendantsAndSelf(expression)
                     .OfType<LiteralExpression>()
                     .Where(l => l.Radix != IntegerLiteralRadix.Decimal))
        {
            // The radix comes from the token the lexer matched, not from re-reading the text, so
            // a string constant that merely contains "0x" is not mistaken for one of these.
            Report(
                file,
                $"{subject} uses the non-decimal integer literal '{literal.Text}'",
                literal.Line ?? expression.Line,
                literal.Column ?? expression.Column,
                PostgresVersionedFeature.NonDecimalIntegerLiteral,
                schemaProvider,
                warnings);
        }
    }

    private static void Report(
        IFile file,
        string subject,
        int? line,
        int? column,
        PostgresVersionedFeature feature,
        PostgresqlDatabaseSchemaProvider schemaProvider,
        List<SqlSourceDiagnostic> warnings)
    {
        if (schemaProvider.MajorVersion >= feature.MinimumMajorVersion)
        {
            return;
        }

        warnings.Add(new SqlSourceDiagnostic(
            $"{subject}, which requires PostgreSQL {feature.MinimumMajorVersion} or later, but "
            + $"this project targets PostgreSQL {schemaProvider.MajorVersion}. "
            + $"See {feature.DocumentationUrl}.",
            file.Name, line, column, SqlSourceDiagnostic.FeatureNotInTargetVersion));
    }
}
