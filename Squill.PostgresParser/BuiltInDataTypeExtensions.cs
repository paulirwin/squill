using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public static class BuiltInDataTypeExtensions
{
    public static string CanonicalName(this PostgresBuiltInDataType dataType)
    {
        return dataType switch
        {
            PostgresBuiltInDataType.Varchar => "character varying",
            PostgresBuiltInDataType.Char => "character",
            PostgresBuiltInDataType.SmallInt => "smallint",
            PostgresBuiltInDataType.Integer => "integer",
            PostgresBuiltInDataType.BigInt => "bigint",
            PostgresBuiltInDataType.Decimal => "numeric",
            PostgresBuiltInDataType.Real => "real",
            PostgresBuiltInDataType.Double => "double precision",
            // The serial types are shorthand for a sequence-backed integer column, not
            // types in their own right: a serial column's type is reported by
            // format_type()/information_schema as the underlying integer type. Canonicalize
            // to that so a parsed model can hash-match one extracted from a live database
            // (issue #121).
            // https://www.postgresql.org/docs/current/datatype-numeric.html#DATATYPE-SERIAL
            PostgresBuiltInDataType.SmallSerial => "smallint",
            PostgresBuiltInDataType.Serial => "integer",
            PostgresBuiltInDataType.BigSerial => "bigint",
            // information_schema and format_type() spell the without-time-zone variants
            // out in full ("timestamp without time zone", "time without time zone"), so the
            // canonical names must match or a parsed model won't hash-match one extracted
            // from the database (issue #97).
            // https://www.postgresql.org/docs/current/datatype-datetime.html
            PostgresBuiltInDataType.Timestamp => "timestamp without time zone",
            PostgresBuiltInDataType.TimestampWithTimeZone => "timestamp with time zone",
            PostgresBuiltInDataType.Date => "date",
            PostgresBuiltInDataType.Time => "time without time zone",
            PostgresBuiltInDataType.TimeWithTimeZone => "time with time zone",
            PostgresBuiltInDataType.Interval => "interval",
            PostgresBuiltInDataType.Text => "text",
            PostgresBuiltInDataType.TSVector => "tsvector",
            PostgresBuiltInDataType.TSQuery => "tsquery",
            PostgresBuiltInDataType.Boolean => "boolean",
            PostgresBuiltInDataType.ByteArray => "bytea",
            // A bare `bit` is fixed-length `bit(1)`; `bit varying` (varbit) is unbounded.
            // format_type() renders these as "bit" and "bit varying" (see
            // https://www.postgresql.org/docs/current/datatype-bit.html).
            PostgresBuiltInDataType.Bit => "bit",
            PostgresBuiltInDataType.BitVarying => "bit varying",
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
        };
    }
}