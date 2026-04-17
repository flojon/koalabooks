var builder = DistributedApplication.CreateBuilder(args);

var sql = builder.AddSqlServer("sql")
    .WithDataVolume("koalabooks-sql-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("koalabooks");

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web")
    .WithReference(sql)
    .WaitFor(sql);

builder.Build().Run();
