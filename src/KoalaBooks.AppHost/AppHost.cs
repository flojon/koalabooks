using KoalaBooks.AppHostSupport;

var builder = DistributedApplication.CreateBuilder(args);

var postgresVolumeName = VolumeNaming.GetVolumeName(Environment.GetEnvironmentVariable("ASPIRE_DB_SUFFIX"));
Console.WriteLine($"[koalabooks] Postgres data volume: {postgresVolumeName}");

var appUserPassword = builder.AddParameter("app-user-password", secret: true);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(postgresVolumeName)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithInitFiles("../../db-init")
    .WithEnvironment("APP_USER_PASSWORD", appUserPassword)
    .WithEnvironment("POSTGRES_DB", "koalabooks");

var koalabooksDb = postgres.AddDatabase("koalabooks");

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web")
    .WithReference(koalabooksDb)
    .WithEnvironment(ctx =>
    {
        var endpoint = postgres.GetEndpoint("tcp");
        ctx.EnvironmentVariables["ConnectionStrings__koalabooks_app"] = ReferenceExpression.Create(
            $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database=koalabooks;Username=app_user;Password={appUserPassword.Resource}");
    })
    // Matches DemoDataSeeder.DemoUserEmail, so a fresh `aspire start` gets a working Admin login.
    .WithEnvironment("AdminSeed__Email", "admin@koalabooks.local")
    .WaitFor(postgres);

builder.Build().Run();
