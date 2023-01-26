using Squill.Provider.Postgres.Syntax;

namespace Squill.Provider.Postgres.AntlrParser;

public interface IPostgresParser
{
    Root Parse(string text);
}