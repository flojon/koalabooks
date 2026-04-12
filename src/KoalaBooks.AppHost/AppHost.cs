var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web");

builder.Build().Run();
