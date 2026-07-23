using Squill.Core;
using Squill.TestFramework;

namespace Squill.IntegrationTests;

/// <summary>
/// An <see cref="Squill.TestFramework.EmbeddedResourceFile"/> bound to this test assembly, so the
/// integration tests can load their embedded <c>.sql</c> fixtures by resource name without having
/// to pass the assembly at every call site. The resource-loading logic lives in the shared
/// framework type; this only supplies the owning assembly.
/// </summary>
public sealed class EmbeddedResourceFile(string name, FileKind kind)
    : Squill.TestFramework.EmbeddedResourceFile(name, kind, typeof(EmbeddedResourceFile));
