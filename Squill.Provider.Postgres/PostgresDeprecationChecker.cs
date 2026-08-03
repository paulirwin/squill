using Squill.Core;
using Squill.PostgresParser.Syntax;

namespace Squill.Provider.Postgres;

/// <summary>
/// Reports source constructs PostgreSQL still accepts but advises against (issue #190), as SQ1006
/// warnings.
///
/// <para>
/// Deliberately separate from <see cref="PostgresTargetVersionChecker"/>. That one asks whether
/// the declared target is new enough for the source; this asks whether the source should be
/// written this way at all, which no target version bears on. Folding them together would attach
/// two incompatible remedies to one report — "raise your target" and "rewrite this" — only one of
/// which is ever right.
/// </para>
///
/// <para>
/// Only column types are examined, because <c>time with time zone</c> is the only entry and it is
/// a type. The check is deliberately narrow: see <see cref="PostgresDeprecatedFeature"/> for the
/// candidates that were investigated and found <em>not</em> to be deprecated.
/// </para>
/// </summary>
internal static class PostgresDeprecationChecker
{
    /// <summary>
    /// Adds a diagnostic for each deprecated construct a column in <paramref name="column"/>
    /// declares, naming it as <paramref name="columnName"/> in the message.
    /// </summary>
    public static void CheckColumn(
        IFile file,
        ColumnDefinition column,
        string columnName,
        List<SqlSourceDiagnostic> warnings)
    {
        if (DeprecatedFeatureOfType(column.DataType) is not { } feature)
        {
            return;
        }

        // Unlike MariaDB's, a Postgres ColumnDefinition is a SyntaxNode and records its own
        // position, so the warning points at the column rather than the whole statement.
        // "Not recommended" rather than "will be removed", and the remedy is phrased as a
        // suggestion rather than a preparation: PostgreSQL supports this type for SQL-standard
        // compliance and never says it is going away, so promising a removal would overstate
        // what the cited page actually claims.
        warnings.Add(new SqlSourceDiagnostic(
            $"Column '{columnName}' is declared {feature.Description}, which PostgreSQL "
            + $"documents as not recommended. Instead, {feature.Remedy}. "
            + $"See {feature.DocumentationUrl}.",
            file.Name, column.Line, column.Column,
            SqlSourceDiagnostic.DeprecatedConstruct));
    }

    // The deprecated construct a declared type amounts to, or null. An array's element type is
    // what carries the deprecation: timetz[] is as much a time-with-time-zone declaration as
    // timetz is, and only the element type says so.
    private static PostgresDeprecatedFeature? DeprecatedFeatureOfType(DataType dataType)
        => dataType switch
        {
            ArrayDataType array => DeprecatedFeatureOfType(array.ElementType),

            // Both spellings, since the parser resolves the `timetz` alias to the same built-in as
            // the spelled-out keyword form (issue #197). They are one type by two names — Postgres
            // itself reports `time with time zone` for a column declared `timetz` — so matching
            // only one would make the warning trivially avoidable by choosing the other.
            BuiltInDataType { Type: PostgresBuiltInDataType.TimeWithTimeZone } =>
                PostgresDeprecatedFeature.TimeWithTimeZone,

            _ => null,
        };
}
