// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using System.Collections.Immutable;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;

namespace Squill.Aspire.Hosting;

public static class DistributedApplicationBuilderExtensions
{
    extension(IDistributedApplicationBuilder builder)
    {
        public IResourceBuilder<SquillProjectResource> AddSquillProject<TProject>([ResourceName] string name)
            where TProject : IProjectMetadata, new()
        {
            ArgumentNullException.ThrowIfNull(builder);
            ArgumentNullException.ThrowIfNull(name);

            var projectAnnotation = new TProject();

            return builder.AddSquillDacPacResource(name, new SquillProjectResource(name), [ new(CustomResourceKnownProperties.Source, projectAnnotation.ProjectPath) ])
                .WithAnnotation(projectAnnotation);
        }

        private IResourceBuilder<T> AddSquillDacPacResource<T>(string name,
            T resource,
            ImmutableArray<ResourcePropertySnapshot> properties)
            where T : IResourceWithSquillDacpac
        {
            return builder.AddResource(resource)
                .WithIconName("DatabaseArrowUp")
                .WithInitialState(new CustomResourceSnapshot
                {
                    Properties = properties,
                    ResourceType = "SquillProject",
                    State = KnownResourceStates.Waiting
                })
                .ExcludeFromManifest();
        }
    }
}
