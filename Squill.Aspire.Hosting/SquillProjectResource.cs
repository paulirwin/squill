// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using System.Reflection;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Build.Evaluation;
using Squill.Core;

namespace Squill.Aspire.Hosting;

[AspireExport(ExposeProperties = true)]
public class SquillProjectResource(string name) : Resource(name), IResourceWithSquillDacpac, IResourceWithWaitSupport
{
    public string? DacpacPath => this.TryGetLastAnnotation<SquillDacpacMetadataAnnotation>(out var dacpacMetadata)
        ? dacpacMetadata.DacpacPath
        : null;

    string IResourceWithSquillDacpac.GetDacpacPath()
    {
        if (this.TryGetLastAnnotation<IProjectMetadata>(out var projectMetadata))
        {
            var projectPath = projectMetadata.ProjectPath;
            using var projectCollection = new ProjectCollection();

            var attr = projectMetadata.GetType().Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>();
            if (attr is not null)
                projectCollection.SetGlobalProperty("Configuration", attr.Configuration);

            var project = projectCollection.LoadProject(projectPath);

            // .squillproj has a SquillTargetPath property, so try that first
            var targetPath = project.GetPropertyValue("SquillTargetPath");
            if (string.IsNullOrWhiteSpace(targetPath))
            {
                targetPath = project.GetPropertyValue("TargetPath");
            }

            return targetPath;
        }

        if (this.TryGetLastAnnotation<SquillDacpacMetadataAnnotation>(out var dacpacMetadata))
        {
            return dacpacMetadata.DacpacPath;
        }

        throw new InvalidOperationException($"Unable to locate Squill Database project package for resource {Name}.");
    }

    DeployOptions IResourceWithSquillDacpac.GetDeployOptions()
    {
        var options = DeployOptions.CreateDefault();

        if (this.TryGetLastAnnotation<ConfigureSquillDeployOptionsAnnotation>(out var configureAnnotation))
        {
            configureAnnotation.ConfigureDeploymentOptions(options);
        }

        return options;
    }
}
