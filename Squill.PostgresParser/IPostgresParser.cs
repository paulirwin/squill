using Squill.PostgresParser.Syntax;

namespace Squill.PostgresParser;

public interface IPostgresParser
{
    Root Parse(string text);
}