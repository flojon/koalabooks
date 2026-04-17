var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("koalabooks-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("koalabooks");

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web")
    .WithReference(postgres)
    .WaitFor(postgres);

builder.Build().Run();
