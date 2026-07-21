namespace Squill.MariaDbParser;

/// <summary>Thrown when the MariaDB parser cannot parse the given SQL text.</summary>
public class MariaDbParseException : Exception
{
    public MariaDbParseException(string message) : base(message)
    {
    }

    public MariaDbParseException(string message, Exception innerException) : base(message, innerException)
    {
    }

    public MariaDbParseException(string message, int? line, int? column, Exception? innerException = null)
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
