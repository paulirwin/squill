// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Squill.Provider.MariaDb;

namespace Squill.Aspire.Hosting.MariaDb;

public static class SquillProjectBuilderExtensions
{
    [AspireExport("withSquillDatabaseReference", MethodName = "withReference", Description = "Publishes the Squill database project to a MariaDB/MySQL database resource.")]
    public static IResourceBuilder<SquillProjectResource> WithReference(
        this IResourceBuilder<SquillProjectResource> builder, IResourceBuilder<MySqlDatabaseResource> target)
    {
        return builder.WithSquillDeploymentReference(new MariaDbSquillProvider(), target, target.Resource.DatabaseName);
    }
}
