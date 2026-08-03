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
        warnings.Add(new SqlSourceDiagnostic(
            $"Column '{columnName}' is declared {feature.Description}, which PostgreSQL "
            + $"documents as not recommended. To prepare, {feature.Remedy}. "
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

            // The keyword spelling, which the parser resolves to a built-in.
            BuiltInDataType { Type: PostgresBuiltInDataType.TimeWithTimeZone } =>
                PostgresDeprecatedFeature.TimeWithTimeZone,

            // The `timetz` alias, which is not a keyword in the grammar and so arrives unresolved,
            // carrying the name as written. It is the same type by another name — Postgres itself
            // reports `time with time zone` for a column declared `timetz` — so leaving it out
            // would make the warning trivially avoidable by choosing the shorter spelling.
            //
            // Matched case-insensitively because Postgres folds unquoted identifiers to lower
            // case. Not matched schema-qualified (pg_catalog.timetz): the parser rejects a
            // qualified generic type before the model builder sees it, so stripping a schema here
            // would be handling a case that cannot arrive.
            UnresolvedDataType unresolved
                when string.Equals(unresolved.TypeName, "timetz", StringComparison.OrdinalIgnoreCase) =>
                PostgresDeprecatedFeature.TimeWithTimeZone,

            _ => null,
        };
}
