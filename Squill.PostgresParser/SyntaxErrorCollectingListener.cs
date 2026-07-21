using Antlr4.Runtime;

namespace Squill.PostgresParser;

/// <summary>
/// Collects ANTLR lexer and parser syntax errors — message plus 1-based line/column —
/// instead of letting the default listeners print them to the console, so the parser can
/// surface them as a <see cref="PostgresParseException"/> carrying the error's position.
/// </summary>
internal sealed class SyntaxErrorCollectingListener : BaseErrorListener, IAntlrErrorListener<int>
{
    public List<(string Message, int Line, int Column)> Errors { get; } = [];

    public override void SyntaxError(TextWriter output, IRecognizer recognizer, IToken offendingSymbol,
        int line, int charPositionInLine, string msg, RecognitionException e)
        => Errors.Add((TrimMessage(msg), line, charPositionInLine + 1));

    public void SyntaxError(TextWriter output, IRecognizer recognizer, int offendingSymbol,
        int line, int charPositionInLine, string msg, RecognitionException e)
        => Errors.Add((TrimMessage(msg), line, charPositionInLine + 1));

    // ANTLR's "expecting {...}" set for a grammar this large can run to hundreds of
    // tokens; a diagnostic that long is noise, so drop the set when it dominates.
    private static string TrimMessage(string message)
    {
        const int maxLength = 160;

        if (message.Length <= maxLength)
        {
            return message;
        }

        var expectingIndex = message.IndexOf(" expecting ", StringComparison.Ordinal);

        return expectingIndex > 0 ? message[..expectingIndex] : message[..maxLength];
    }
}
