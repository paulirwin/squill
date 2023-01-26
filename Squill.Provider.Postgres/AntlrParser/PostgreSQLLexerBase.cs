using System.Diagnostics;
using Antlr4.Runtime;

namespace Squill.Provider.Postgres.AntlrParser;

public abstract class PostgreSQLLexerBase : Lexer
{
    protected static readonly Queue<string> tags = new();

    protected PostgreSQLLexerBase(ICharStream input, TextWriter output, TextWriter errorOutput) 
        : base(input, output, errorOutput)
    {
    }

    private IIntStream getInputStream() { return InputStream; }
    
    public override string[] RuleNames => throw new NotImplementedException();

    public override IVocabulary Vocabulary => throw new NotImplementedException();

    public override string GrammarFileName => throw new NotImplementedException();

    public void pushTag() { tags.Enqueue(this.Text); }

    public bool isTag() { return this.Text.Equals(tags.Peek()); }

    public void popTag()
    {
        tags.Dequeue();
    }

    public void UnterminatedBlockCommentDebugAssert()
    {
        Debug.Assert(InputStream.LA(1) == -1 /*EOF*/);
    }

    public bool checkLA(int c)
    {
        return getInputStream().LA(1) != c;
    }

    public bool charIsLetter()
    {
        return Char.IsLetter((char)InputStream.LA(-1));
    }

    public void HandleNumericFail()
    {
        InputStream.Seek(getInputStream().Index - 2);
        Type = PostgreSQLLexer.Integral;
    }

    public void HandleLessLessGreaterGreater()
    {
        if (Text == "<<") Type = PostgreSQLLexer.LESS_LESS;
        if (Text == ">>") Type = PostgreSQLLexer.GREATER_GREATER;
    }

    public bool CheckIfUtf32Letter()
    {
        return char.IsLetter(char.ConvertFromUtf32(char.ConvertToUtf32((char)InputStream.LA(-2),
                (char)InputStream.LA(-1)))
            .Substring(0)[0]);
    }

}
