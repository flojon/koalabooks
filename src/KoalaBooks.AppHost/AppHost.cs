using KoalaBooks.AppHostSupport;

var builder = DistributedApplication.CreateBuilder(args);

var postgresVolumeName = VolumeNaming.GetVolumeName(Environment.GetEnvironmentVariable("ASPIRE_DB_SUFFIX"));
Console.WriteLine($"[koalabooks] Postgres data volume: {postgresVolumeName}");

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(postgresVolumeName)
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("koalabooks");

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web")
    .WithReference(postgres)
    .WaitFor(postgres);

builder.Build().Run();
