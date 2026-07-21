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
}
