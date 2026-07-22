// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using Microsoft.Extensions.Logging;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Aspire.Hosting;

public interface ISquillDacpacDeployer
{
    Task Deploy(ISquillProvider provider,
        string dacpacPath,
        DeployOptions options,
        string connectionString,
        string? targetDatabaseName,
        ILogger logger,
        CancellationToken cancellationToken);
}
