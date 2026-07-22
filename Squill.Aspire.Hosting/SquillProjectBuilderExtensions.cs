// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Eventing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Squill.Core;
using Squill.Dacpac;

namespace Squill.Aspire.Hosting;

public static class SquillProjectBuilderExtensions
{
    extension(IResourceBuilder<SquillProjectResource> builder)
    {
        public IResourceBuilder<SquillProjectResource> WithConfigureDeployOptions(Action<DeployOptions> configureDeploymentOptions)
            => builder.InternalWithConfigureDeployOptions(configureDeploymentOptions);
    }

    /// <param name="builder">The Squill project resource builder.</param>
    /// <typeparam name="TResource">The type of the Squill project resource.</typeparam>
    extension<TResource>(IResourceBuilder<TResource> builder) where TResource : IResourceWithSquillDacpac
    {
        internal IResourceBuilder<TResource> InternalWithConfigureDeployOptions(Action<DeployOptions> configureDeploymentOptions)
        {
            ArgumentNullException.ThrowIfNull(builder, nameof(builder));
            ArgumentNullException.ThrowIfNull(configureDeploymentOptions);

            return builder
                .WithAnnotation(new ConfigureSquillDeployOptionsAnnotation(configureDeploymentOptions));
        }

        /// <summary>
        /// Adds a reference to the target database for Squill dacpac deployment.
        /// </summary>
        /// <param name="provider">The Squill provider.</param>
        /// <param name="target">The target database resource builder.</param>
        /// <param name="targetDatabaseName">The target database name. If <c>null</c>, it must be in the connection string.</param>
        /// <returns>The updated resource builder.</returns>
        /// <remarks>
        /// This is a lower-level API and is typically used by higher-level extension methods for specific database types.
        /// Consider using the <c>WithReference</c> methods provided by the <c>Squill.Aspire.Hosting.*</c> packages for
        /// stronger typing and easier configuration.
        /// </remarks>
        public IResourceBuilder<TResource> WithSquillDeploymentReference(ISquillProvider provider,
            IResourceBuilder<IResourceWithConnectionString> target,
            string? targetDatabaseName)
        {
            builder.ApplicationBuilder.Services.TryAddSingleton<ISquillDacpacDeployer, SquillDacpacDeployer>();
            builder.ApplicationBuilder.Services.TryAddSingleton<SquillProjectPublishService>();

            builder.WithParentRelationship(target.Resource);

            target.OnResourceReady(async (targetResource, evt, ct) =>
            {
                if (builder.Resource.TryGetAnnotationsOfType<ExplicitStartupAnnotation>(out _))
                {
                    return;
                }

                await ExecuteResource(provider, builder.Resource, target.Resource, targetDatabaseName, evt.Services, ct);
            });

            var commandOptions = new CommandOptions
            {
                IconName = "ArrowReset",
                IconVariant = IconVariant.Filled,
                IsHighlighted = true,
                Description = "Deploy the SQL Server Database Project to the target database.",
                UpdateState = context =>
                {
                    var state = context.ResourceSnapshot?.State?.Text;

                    return state == KnownResourceStates.Running || state == KnownResourceStates.Starting
                        ? ResourceCommandState.Disabled
                        : ResourceCommandState.Enabled;
                },
            };

            builder.WithCommand("deploy", "Deploy", async context =>
            {
                await ExecuteResource(provider, builder.Resource, target.Resource, targetDatabaseName, context.ServiceProvider, context.CancellationToken);
                return new ExecuteCommandResult { Success = true };
            }, commandOptions);

            return builder;
        }
    }

    private static async Task ExecuteResource<TResource>(ISquillProvider provider, TResource resource, IResourceWithConnectionString target, string? targetDatabaseName, IServiceProvider serviceProvider, CancellationToken ct)
        where TResource : IResourceWithSquillDacpac
    {
        var eventing = serviceProvider.GetRequiredService<IDistributedApplicationEventing>();
        await eventing.PublishAsync(new BeforeResourceStartedEvent(resource, serviceProvider), ct);

        var service = serviceProvider.GetRequiredService<SquillProjectPublishService>();
        await service.PublishSquillProject(provider, resource, target, targetDatabaseName, ct);
    }
}
