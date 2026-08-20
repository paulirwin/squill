namespace Squill.PostgresParser.Syntax;

public enum PostgresBuiltInBinaryOperator
{
    Exponentiation,
    Multiplication,
    Division,
    Modulo,
    Addition,
    Subtraction,
    LessThan,
    LessThanEqual,
    GreaterThan,
    GreaterThanEqual,
    Equal,
    NotEqual,
    And,
    Or,
    NotIn,
    In,
    LeftShift,
    RightShift,
    Like,
    NotLike,
    ILike,
    NotILike,
    SimilarTo,
    NotSimilarTo,

    // Null-safe inequality: unlike <>, it treats two nulls as equal and a null against a
    // non-null as distinct. There is no NotDistinctFrom counterpart because PostgreSQL does
    // not store one -- measured, IS NOT DISTINCT FROM is rewritten to a NOT around this.
    IsDistinctFrom
}