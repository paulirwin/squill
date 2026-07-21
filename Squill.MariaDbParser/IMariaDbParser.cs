using Squill.MariaDbParser.Syntax;

namespace Squill.MariaDbParser;

public interface IMariaDbParser
{
    Root Parse(string text);
}
