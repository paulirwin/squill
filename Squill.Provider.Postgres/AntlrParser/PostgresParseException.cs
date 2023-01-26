namespace Squill.Provider.Postgres.AntlrParser;

public class PostgresParseException : Exception
{
    public PostgresParseException(string message)
        : base(message)
    {
    }
}