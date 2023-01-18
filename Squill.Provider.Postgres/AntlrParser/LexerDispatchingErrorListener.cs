using Antlr4.Runtime;

namespace Squill.Provider.Postgres.AntlrParser;

public class LexerDispatchingErrorListener : IAntlrErrorListener<int>
{
    private readonly Lexer? _parent;

    public LexerDispatchingErrorListener(Lexer? parent)
    {
        _parent = parent;
    }

    public void SyntaxError(
        TextWriter output,
        IRecognizer recognizer,
        int offendingSymbol,
        int line,
        int charPositionInLine,
        string msg,
        RecognitionException e)
    {
        var foo = new ProxyErrorListener<int>(_parent?.ErrorListeners ?? new List<IAntlrErrorListener<int>>());
        foo.SyntaxError(output, recognizer, offendingSymbol, line, charPositionInLine, msg, e);
    }
}
