namespace Squill.Templates.Tests;

/// <summary>
/// Serializes the template tests: each spawns <c>dotnet new</c> / <c>dotnet build</c>
/// subprocesses, so running them one at a time avoids restore/build contention.
/// </summary>
[CollectionDefinition("Template", DisableParallelization = true)]
public class TemplateCollection;
