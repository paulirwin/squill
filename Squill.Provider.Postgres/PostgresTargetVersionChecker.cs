using Squill.Core;
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
/// SQ1003 through MSBuild's <c>WarningsAsErrors</c>.
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
