using Antlr4.Runtime;
using Antlr4.Runtime.Misc;

namespace Squill.Provider.Postgres.AntlrParser;

public class CaseChangingCharStream : ICharStream
{
    private readonly ICharStream _stream;
    private readonly bool _upper;

    public CaseChangingCharStream(ICharStream stream, bool upper)
    {
        _stream = stream;
        _upper = upper;
    }

    public void Consume() => _stream.Consume();

    public int LA(int i)
    {
        int c = _stream.LA(i);

        if (c <= 0)
        {
            return c;
        }

        // HACK.PI: this probably will not work beyond 8-bit characters
        return _upper ? char.ToUpperInvariant((char)c) : char.ToLowerInvariant((char)c);
    }

    public int Mark() => _stream.Mark();

    public void Release(int marker) => _stream.Release(marker);

    public void Seek(int index) => _stream.Seek(index);

    public int Index => _stream.Index;

    public int Size => _stream.Size;

    public string SourceName => _stream.SourceName;

    public string GetText(Interval interval) => _stream.GetText(interval);
}