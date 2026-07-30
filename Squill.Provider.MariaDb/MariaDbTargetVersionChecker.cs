using Squill.Core;
using Squill.MariaDbParser.Syntax;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Reports source constructs that the target engine and major version do not accept (issue
/// #142), as SQ1003 (introduced in a later version of this engine) or SQ1004 (absent from this
/// engine at any version).
///
/// <para>
/// This runs at build time because the alternative is finding out mid-deploy. The target
/// version was already recorded and checked, but only against the server's reported version
/// when the deploy started — so a too-new construct built cleanly, deployed until it reached
/// that statement, and failed there with earlier statements already applied.
/// </para>
///
/// <para>
/// The construct is still modeled. Dropping it would build a model that silently means
/// something other than the source says, which is the failure #141 called out for typed
/// literals; the warning is the whole of the response, and a project that wants it fatal
/// escalates SQ1003 through <c>MSBuildWarningsAsErrors</c>.
/// </para>
/// </summary>
internal static class MariaDbTargetVersionChecker
{
    /// <summary>
    /// Adds a diagnostic for each construct in <paramref name="statement"/> that the target
    /// does not accept. Only column types are examined today: they are what the syntax tree
    /// carries losslessly (<see cref="DataType.TypeName"/> is canonical and lower-cased), and a
    /// check that quietly missed half its cases would be worse than none.
    /// </summary>
    public static void Check(
        IFile file,
        CreateTableStatement statement,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider,
        List<SqlSourceDiagnostic> warnings)
    {
        foreach (var column in statement.Elements.OfType<ColumnDefinition>())
        {
            var feature = FeatureOfType(column.DataType);

            if (feature is not { } versioned)
            {
                continue;
            }

            Report(file, statement, column, versioned, schemaProvider, warnings);
        }
    }

    // The features that a column's declared type alone identifies. Matched on the canonical
    // lower-cased type name, so a source spelling of VECTOR, Vector or vector all resolve here.
    private static MariaDbVersionedFeature? FeatureOfType(DataType dataType)
        => dataType.TypeName switch
        {
            "vector" => MariaDbVersionedFeature.Vector,
            "uuid" => MariaDbVersionedFeature.Uuid,
            _ => null,
        };

    private static void Report(
        IFile file,
        CreateTableStatement statement,
        ColumnDefinition column,
        MariaDbVersionedFeature feature,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider,
        List<SqlSourceDiagnostic> warnings)
    {
        var subject = $"Column '{statement.Name}.{column.Name}' is declared "
            + $"{feature.Description}";

        // A MariaDB ColumnDefinition is an ITableElement rather than a SyntaxNode, so it
        // records no position of its own; the statement is all the anchor there is. The
        // message names the column, which is what makes it findable within the statement.
        var line = statement.Line;
        var col = statement.Column;

        // The citation is the targeted engine's own, never the other's: pointing a MySQL warning
        // at MariaDB's VECTOR page would cite something that says nothing about MySQL's boundary.
        // It is omitted entirely rather than substituted when that engine has none to cite.
        var citation = feature.DocumentationUrlFor(schemaProvider) is { } url
            ? $" See {url}."
            : string.Empty;

        // Absent from this engine at any version: "too new" would send the author looking for
        // an upgrade that does not exist, so this is its own diagnostic.
        if (feature.MinimumMajorVersionFor(schemaProvider) is not { } minimum)
        {
            warnings.Add(new SqlSourceDiagnostic(
                $"{subject}, which {schemaProvider.ProviderName} does not support at any "
                + $"version.{citation}",
                file.Name, line, col, SqlSourceDiagnostic.FeatureNotSupportedByEngine));

            return;
        }

        if (schemaProvider.MajorVersion >= minimum)
        {
            return;
        }

        var note = feature.Note is { } text ? $" {text}" : string.Empty;

        warnings.Add(new SqlSourceDiagnostic(
            $"{subject}, which requires {schemaProvider.ProviderName} {minimum} or later, but "
            + $"this project targets {schemaProvider.ProviderName} {schemaProvider.MajorVersion}."
            + $"{citation}{note}",
            file.Name, line, col, SqlSourceDiagnostic.FeatureNotInTargetVersion));
    }
}
