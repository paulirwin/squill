// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using Microsoft.Extensions.Logging;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Aspire.Hosting;

public class SquillDacpacDeployer : ISquillDacpacDeployer
{
    public async Task Deploy(ISquillProvider provider, string dacpacPath, DeployOptions options, string targetConnectionString, string? targetDatabaseName, ILogger deploymentLogger, CancellationToken cancellationToken)
    {
        var progress = new LoggerProgress(deploymentLogger);
        await DacpacProviderDispatch.DeployFromFileAsync(provider, dacpacPath, targetConnectionString, targetDatabaseName, progress: progress, options: options, cancellationToken: cancellationToken);
    }
}
