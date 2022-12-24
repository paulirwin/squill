namespace Squill.Core;

public class DatabaseParameter<T> : IDatabaseParameter
{
    public DatabaseParameter(string parameterName, T parameterValue)
    {
        ParameterName = parameterName;
        ParameterValue = parameterValue;
    }

    public string ParameterName { get; }

    public T ParameterValue { get; }
    
    object? IDatabaseParameter.ParameterValue => ParameterValue;
}