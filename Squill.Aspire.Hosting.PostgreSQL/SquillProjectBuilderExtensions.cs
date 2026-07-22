// This file is derived from CommunityToolkit.Aspire, licensed under the MIT License.
// Copyright (c) .NET Foundation and Contributors. See THIRD-PARTY-NOTICES.md for details.

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Squill.Provider.Postgres;

namespace Squill.Aspire.Hosting.PostgreSQL;

public static class SquillProjectBuilderExtensions
{
    [AspireExport("withSquillDatabaseReference", MethodName = "withReference", Description = "Publishes the Squill database project to a PostgreSQL database resource.")]
    public static IResourceBuilder<SquillProjectResource> WithReference(
        this IResourceBuilder<SquillProjectResource> builder, IResourceBuilder<PostgresDatabaseResource> target)
        => builder.WithSquillDeploymentReference(new PostgresSquillProvider(), target, target.Resource.DatabaseName);
}
