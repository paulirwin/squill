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
            PostgresBuiltInDataType.SmallSerial => "smallserial",
            PostgresBuiltInDataType.Serial => "serial",
            PostgresBuiltInDataType.BigSerial => "bigserial",
            PostgresBuiltInDataType.Timestamp => "timestamp",
            PostgresBuiltInDataType.TimestampWithTimeZone => "timestamp with time zone",
            PostgresBuiltInDataType.Date => "date",
            PostgresBuiltInDataType.Time => "time",
            PostgresBuiltInDataType.TimeWithTimeZone => "time with time zone",
            PostgresBuiltInDataType.Interval => "interval",
            PostgresBuiltInDataType.Text => "text",
            PostgresBuiltInDataType.TSVector => "tsvector",
            PostgresBuiltInDataType.TSQuery => "tsquery",
            PostgresBuiltInDataType.Boolean => "boolean",
            PostgresBuiltInDataType.ByteArray => "bytea",
            _ => throw new ArgumentOutOfRangeException(nameof(dataType), dataType, null)
        };
    }
}