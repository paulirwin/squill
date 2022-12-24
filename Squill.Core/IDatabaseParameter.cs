namespace Squill.Core;

public interface IDatabaseParameter
{
    string ParameterName { get; }

    object? ParameterValue { get; }
}