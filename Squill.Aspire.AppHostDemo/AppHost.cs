using Squill.Aspire.Hosting;
using Squill.Aspire.Hosting.MariaDb;
using Squill.Aspire.Hosting.PostgreSQL;

var builder = DistributedApplication.CreateBuilder(args);

var pgsqldb = builder.AddPostgres("postgres")
    .AddDatabase("squill-pgsql");

builder.AddSquillProject<Projects.PostgresSampleDatabase>("squill-pgsql-sample")
    .WithReference(pgsqldb)
    .WithConfigureDeployOptions(options =>
    {
        options.AllowTableRebuild = false;
        options.BlockOnPossibleDataLoss = true;
        options.DropObjectsNotInSource = false;
    });

var mysqldb = builder.AddMySql("mysql")
    .AddDatabase("squill-mysql");

builder.AddSquillProject<Projects.MariaDbSampleDatabase>("squill-mysql-sample")
    .WithReference(mysqldb)
    .WithConfigureDeployOptions(options =>
    {
        options.AllowTableRebuild = false;
        options.BlockOnPossibleDataLoss = true;
        options.DropObjectsNotInSource = false;
    });

var mariadb = builder.AddMySql("mariadb")
    .WithImage("mariadb:latest")
    .AddDatabase("squill-mariadb");

builder.AddSquillProject<Projects.MariaDbSampleDatabase>("squill-mariadb-sample")
    .WithReference(mariadb)
    .WithConfigureDeployOptions(options =>
    {
        options.AllowTableRebuild = false;
        options.BlockOnPossibleDataLoss = true;
        options.DropObjectsNotInSource = false;
    });

builder.Build().Run();
