namespace Squill.PostgresParser;

public class PostgresParseException : Exception
{
    public PostgresParseException(string message)
        : base(message)
    {
    }

    public PostgresParseException(string message, int? line, int? column, Exception? innerException = null)
        : base(message, innerException)
    {
        Line = line;
        Column = column;
    }

    /// <summary>The 1-based line of the error in the parsed text, or null when unknown.</summary>
    public int? Line { get; }

    /// <summary>The 1-based column of the error in the parsed text, or null when unknown.</summary>
    public int? Column { get; }
}
