namespace Squill.PostgresParser.Syntax;

/// <summary>Which end(s) of the string a <see cref="TrimExpression"/> trims.</summary>
public enum TrimSide
{
    /// <summary>Both ends — written as <c>BOTH</c>, or left unwritten (the default).</summary>
    Both,

    Leading,

    Trailing,
}
