// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using Aspire.Hosting.ApplicationModel;

namespace Squill.Aspire.Hosting;

public record SquillDacpacMetadataAnnotation(string DacpacPath) : IResourceAnnotation
{
}
