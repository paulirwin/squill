namespace Squill.PostgresParser.Syntax;

public class PLSQLVariableName : Identifier
{
    public PLSQLVariableName(string variableName)
    {
        VariableName = variableName;
    }

    public override string Name => $":{VariableName}";

    public string VariableName { get; }
}