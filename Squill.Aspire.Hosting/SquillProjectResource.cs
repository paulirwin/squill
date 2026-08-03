// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using System.Reflection;
using System.Runtime.CompilerServices;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Microsoft.Build.Locator;
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
            EnsureMSBuildRegistered();

            return EvaluateTargetPath(projectMetadata);
        }

        if (this.TryGetLastAnnotation<SquillDacpacMetadataAnnotation>(out var dacpacMetadata))
        {
            return dacpacMetadata.DacpacPath;
        }

        throw new InvalidOperationException($"Unable to locate Squill Database project package for resource {Name}.");
    }

    /// <summary>
    /// Registers the .NET SDK's MSBuild assemblies with the current process, once.
    /// </summary>
    /// <remarks>
    /// A .squillproj that references the SDK by name (<c>Sdk="Squill.Sdk/x.y.z"</c>) — the form
    /// consuming repos use — can only be evaluated if the SDK resolvers that ship with the .NET SDK
    /// are available. The Microsoft.Build assemblies this package references cannot resolve a
    /// NuGet-delivered SDK on their own, and evaluation fails with "The SDK 'Squill.Sdk/x.y.z'
    /// specified could not be found." The samples in this repo import the SDK by relative path
    /// instead, which needs no resolver, so they do not exercise this.
    /// </remarks>
    private static void EnsureMSBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered)
        {
            MSBuildLocator.RegisterDefaults();
        }
    }

    /// <summary>
    /// Evaluates the project and returns the path to the DACPAC it produces.
    /// </summary>
    /// <remarks>
    /// Kept out of <see cref="IResourceWithSquillDacpac.GetDacpacPath"/> and not inlined so that no
    /// Microsoft.Build type is resolved before <see cref="EnsureMSBuildRegistered"/> has run —
    /// the JIT loads the assemblies a method body references when that method is prepared, which
    /// would otherwise bind them ahead of registration.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string EvaluateTargetPath(IProjectMetadata projectMetadata)
    {
        using var projectCollection = new Microsoft.Build.Evaluation.ProjectCollection();

        var attr = projectMetadata.GetType().Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>();
        if (attr is not null)
            projectCollection.SetGlobalProperty("Configuration", attr.Configuration);

        var project = projectCollection.LoadProject(projectMetadata.ProjectPath);

        // .squillproj has a SquillTargetPath property, so try that first
        var targetPath = project.GetPropertyValue("SquillTargetPath");
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            targetPath = project.GetPropertyValue("TargetPath");
        }

        return targetPath;
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
