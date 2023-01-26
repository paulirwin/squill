namespace Squill.PostgresParser;

public class PostgresParseException : Exception
{
    public PostgresParseException(string message)
        : base(message)
    {
    }
}