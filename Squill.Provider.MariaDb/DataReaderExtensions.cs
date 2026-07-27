using System.Data.Common;

namespace Squill.Provider.MariaDb;

/// <summary>
/// Name-based accessors over <see cref="DbDataReader"/>. Npgsql exposes column-name
/// overloads of <c>GetString</c> etc. directly; the base <see cref="DbDataReader"/> that
/// MySqlConnector returns only offers ordinal overloads, so these thin wrappers resolve the
/// ordinal by name to keep the model-builder queries readable.
/// </summary>
internal static class DataReaderExtensions
{
    public static string GetString(this DbDataReader reader, string name)
        => reader.GetString(reader.GetOrdinal(name));

    public static int GetInt32(this DbDataReader reader, string name)
        => reader.GetInt32(reader.GetOrdinal(name));

    public static bool IsDBNull(this DbDataReader reader, string name)
        => reader.IsDBNull(reader.GetOrdinal(name));

    public static T GetFieldValue<T>(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        // Support a nullable target type by returning default (null) for a SQL NULL.
        if (reader.IsDBNull(ordinal) && default(T) is null)
        {
            return default!;
        }

        return reader.GetFieldValue<T>(ordinal);
    }

    /// <summary>
    /// Reads a nullable string column, returning null for a SQL NULL and coercing whatever
    /// CLR type the driver returns. The coercion matters for columns the two engines type
    /// differently — information_schema.EVENTS.INTERVAL_VALUE comes back as a string on one
    /// engine and a number on the other — so a plain <c>GetString</c> would throw on one.
    /// An empty string is returned as null: both engines report an absent event comment that
    /// way, and the model omits a facet that was never declared.
    /// </summary>
    public static string? GetStringOrNull(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        var value = Convert.ToString(
            reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);

        return string.IsNullOrEmpty(value) ? null : value;
    }

    /// <summary>
    /// Reads a nullable 64-bit integer column, coercing whatever numeric CLR type the driver
    /// returns. MariaDB and MySQL disagree on the signedness of information_schema numeric
    /// columns (MariaDB returns them as <c>ulong</c>, MySQL as <c>long</c>), so a fixed
    /// <c>GetFieldValue&lt;T&gt;</c> throws on one engine. Coercing the boxed value works for
    /// both.
    /// </summary>
    public static long? GetNullableInt64(this DbDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return Convert.ToInt64(reader.GetValue(ordinal), System.Globalization.CultureInfo.InvariantCulture);
    }
}
