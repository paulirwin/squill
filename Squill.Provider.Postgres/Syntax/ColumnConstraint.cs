namespace Squill.Provider.Postgres.Syntax;

public abstract class ColumnConstraint : SyntaxNode
{
    protected ColumnConstraint(string text)
    {
        Text = text;
    }

    public string Text { get; }
}