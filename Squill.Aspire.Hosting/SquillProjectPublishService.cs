// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using Aspire.Hosting.ApplicationModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Squill.Dacpac;

namespace Squill.Aspire.Hosting;

internal class SquillProjectPublishService(ISquillDacpacDeployer deployer,
    IHostEnvironment hostEnvironment,
    ResourceLoggerService resourceLoggerService,
    ResourceNotificationService resourceNotificationService)
{
    public async Task PublishSquillProject(ISquillProvider provider, IResourceWithSquillDacpac resource, IResourceWithConnectionString target, string? targetDatabaseName, CancellationToken cancellationToken)
    {
        var logger = resourceLoggerService.GetLogger(resource);
        ResourceStateSnapshot? failureState = KnownResourceStates.FailedToStart;

        try
        {
            await resourceNotificationService.PublishUpdateAsync(resource,
                state => state with { State = new ResourceStateSnapshot(KnownResourceStates.Starting, KnownResourceStateStyles.Error) });

            var dacpacPath = resource.GetDacpacPath();
            if (!Path.IsPathRooted(dacpacPath))
            {
                dacpacPath = Path.Combine(hostEnvironment.ContentRootPath, dacpacPath);
            }

            if (!File.Exists(dacpacPath))
            {
                logger.LogError("Squill Database project package not found at path {DacpacPath}.", dacpacPath);
                await resourceNotificationService.PublishUpdateAsync(resource,
                    state => state with { State = new ResourceStateSnapshot(KnownResourceStates.FailedToStart, KnownResourceStateStyles.Error) });
                return;
            }
            else
            {
                logger.LogInformation("Squill Database project package found at path {DacpacPath}.", dacpacPath);
            }

            var options = resource.GetDeployOptions();

            var connectionString = await target.ConnectionStringExpression.GetValueAsync(cancellationToken);
            if (connectionString is null)
            {
                logger.LogError("Failed to retrieve connection string for target database {TargetDatabaseResourceName}.", target.Name);
                await resourceNotificationService.PublishUpdateAsync(resource,
                    state => state with { State = KnownResourceStates.FailedToStart });
                return;
            }

            failureState = KnownResourceStates.Finished;

            await resourceNotificationService.PublishUpdateAsync(resource,
                state => state with {
                    State = KnownResourceStates.Running,
                    StartTimeStamp = DateTime.UtcNow
                });

            await deployer.Deploy(provider, dacpacPath, options, connectionString, targetDatabaseName, logger, cancellationToken);

            await resourceNotificationService.PublishUpdateAsync(resource,
                state => state with {
                    State = KnownResourceStates.Finished,
                    ExitCode = 0,
                    StopTimeStamp = DateTime.UtcNow
                });

        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to publish database project.");

            await resourceNotificationService.PublishUpdateAsync(resource,
                state => state with {
                    State = failureState,
                    ExitCode = failureState == KnownResourceStates.Finished ? 1 : state.ExitCode,
                    StopTimeStamp = DateTime.UtcNow
                });
        }
    }
}
