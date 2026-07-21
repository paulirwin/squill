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
}
