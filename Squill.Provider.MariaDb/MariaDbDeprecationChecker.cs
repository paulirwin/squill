using Squill.Core;
using Squill.MariaDbParser.Syntax;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Reports source constructs the target engine still accepts but documents as scheduled for
/// removal (issue #190), as SQ1006 warnings.
///
/// <para>
/// Deliberately separate from <see cref="MariaDbTargetVersionChecker"/> even though both walk the
/// same columns. That one answers "is the declared target new enough for this source?"; this one
/// answers "will this source keep working?" — a question the target version has no bearing on,
/// since these constructs are accepted by every version in the supported window. Folding them
/// together would mean one report with two incompatible remedies attached.
/// </para>
///
/// <para>
/// A construct is checked here only when the syntax tree carries it losslessly. That is why
/// <see cref="DataType.IsZerofill"/> and <see cref="DataType.CharacterSet"/> were added: the
/// grammar accepted both and the mapper discarded them, and a check that silently saw none of the
/// ZEROFILL columns in a schema would be worse than no check, because a clean build would read as
/// a clean bill of health.
/// </para>
/// </summary>
internal static class MariaDbDeprecationChecker
{
    // Integer types, which are the ones whose display width is deprecated. Spelled out rather
    // than derived from "has a modifier": several non-integer types share the same grammar
    // alternative and carry a modifier that is not a display width at all — bit(8), year(4) and
    // datetime(3) among them — so a shape-based test would report constructs that are not
    // deprecated. The synonyms are included because the deprecation is of the attribute, and
    // MySQL accepts int1(4) exactly as it accepts tinyint(4).
    private static readonly HashSet<string> IntegerTypes =
    [
        "tinyint", "smallint", "mediumint", "int", "integer", "bigint", "middleint",
        "int1", "int2", "int3", "int4", "int8",
    ];

    // The types MySQL's UNSIGNED deprecation names: FLOAT, DOUBLE and DECIMAL "and any synonyms".
    // Integer types are absent deliberately — UNSIGNED on them is not deprecated, and reporting
    // the most common attribute in MySQL schemas would bury the four real findings.
    private static readonly HashSet<string> ApproximateAndFixedPointTypes =
    [
        "float", "float4", "float8", "double", "real",
        "decimal", "dec", "numeric", "fixed",
    ];

    // The floating-point types whose AUTO_INCREMENT is deprecated. Narrower than the set above:
    // MySQL names only FLOAT and DOUBLE here, not DECIMAL.
    private static readonly HashSet<string> FloatingPointTypes =
        ["float", "float4", "float8", "double", "real"];

    /// <summary>
    /// Adds a diagnostic for each deprecated construct in <paramref name="statement"/>.
    /// </summary>
    public static void Check(
        IFile file,
        CreateTableStatement statement,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider,
        List<SqlSourceDiagnostic> warnings)
    {
        foreach (var column in statement.Elements.OfType<ColumnDefinition>())
        {
            foreach (var feature in DeprecatedFeaturesOf(column))
            {
                Report(file, statement, column, feature, schemaProvider, warnings);
            }
        }
    }

    // Every deprecated construct a single column declares. A column can hit more than one — a
    // DOUBLE UNSIGNED AUTO_INCREMENT is three separate deprecations by MySQL's own reckoning —
    // and each is reported, because each has to be fixed on its own.
    private static IEnumerable<MariaDbDeprecatedFeature> DeprecatedFeaturesOf(
        ColumnDefinition column)
    {
        var type = column.DataType;

        if (type.IsZerofill)
        {
            yield return MariaDbDeprecatedFeature.Zerofill;
        }

        // A modifier on an integer type is a display width; on any other type it is a length or a
        // precision, which is why the type name is tested before the modifier.
        if (IntegerTypes.Contains(type.TypeName) && type.Modifiers.Count > 0)
        {
            yield return MariaDbDeprecatedFeature.IntegerDisplayWidth;
        }

        if (type.IsUnsigned && ApproximateAndFixedPointTypes.Contains(type.TypeName))
        {
            yield return MariaDbDeprecatedFeature.FloatingPointUnsigned;
        }

        if (FloatingPointTypes.Contains(type.TypeName)
            && column.Constraints.Any(IsAutoIncrement))
        {
            yield return MariaDbDeprecatedFeature.FloatingPointAutoIncrement;
        }

        // Compared case-insensitively because this is a character-set name as written, not a
        // canonical type name: the parser lower-cases the latter but keeps the former's spelling,
        // and UTF8 declares the deprecated set exactly as utf8 does.
        if (string.Equals(type.CharacterSet, "utf8", StringComparison.OrdinalIgnoreCase))
        {
            yield return MariaDbDeprecatedFeature.Utf8CharacterSet;
        }
    }

    // AUTO_INCREMENT is a column constraint rather than part of the type, and may be wrapped in a
    // CONSTRAINT name like any other.
    private static bool IsAutoIncrement(ColumnConstraint constraint)
        => (constraint is NamedColumnConstraint named ? named.Constraint : constraint)
            is AutoIncrementColumnConstraint;

    private static void Report(
        IFile file,
        CreateTableStatement statement,
        ColumnDefinition column,
        MariaDbDeprecatedFeature feature,
        MariaDbFamilyDatabaseSchemaProvider schemaProvider,
        List<SqlSourceDiagnostic> warnings)
    {
        // Nothing is reported for an engine whose documentation does not deprecate the construct.
        // All five of these are MySQL deprecations that MariaDB's Knowledge Base documents as
        // current functionality, so on MariaDB this check is silent by design.
        if (!feature.IsDeprecatedBy(schemaProvider))
        {
            return;
        }

        // As in the version checker: a MariaDB ColumnDefinition is an ITableElement rather than a
        // SyntaxNode and records no position of its own, so the statement is the only anchor and
        // the message names the column to make it findable within it.
        var citation = feature.DocumentationUrlFor(schemaProvider) is { } url
            ? $" See {url}."
            : string.Empty;

        var note = feature.Note is { } text ? $" {text}" : string.Empty;

        warnings.Add(new SqlSourceDiagnostic(
            $"Column '{statement.Name}.{column.Name}' is declared {feature.Description}, which "
            + $"{schemaProvider.ProviderName} deprecates and expects to remove in a future "
            + $"version. To prepare, {feature.Remedy}.{citation}{note}",
            file.Name, statement.Line, statement.Column,
            SqlSourceDiagnostic.DeprecatedConstruct));
    }
}
