// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using Aspire.Hosting.ApplicationModel;
using Squill.Core;

namespace Squill.Aspire.Hosting;

public record ConfigureSquillDeployOptionsAnnotation(Action<DeployOptions> ConfigureDeploymentOptions) : IResourceAnnotation
{
}
