using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(GetPostgresVolumeName())
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("koalabooks");

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web")
    .WithReference(postgres)
    .WaitFor(postgres);

builder.Build().Run();

static string GetPostgresVolumeName([CallerFilePath] string sourcePath = "")
{
    const string baseName = "koalabooks-postgres-data";
    string volumeName;

    var overrideSuffix = Environment.GetEnvironmentVariable("ASPIRE_DB_SUFFIX");
    if (!string.IsNullOrEmpty(overrideSuffix))
    {
        volumeName = $"{baseName}-{overrideSuffix}";
    }
    else
    {
        var appHostDir = Path.GetDirectoryName(sourcePath)!;
        volumeName = IsMainCheckout(appHostDir)
            ? baseName
            : $"{baseName}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appHostDir)))[..8].ToLowerInvariant()}";
    }

    Console.WriteLine($"[koalabooks] Postgres data volume: {volumeName}");
    return volumeName;
}

static bool IsMainCheckout(string startDir)
{
    for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
    {
        var gitPath = Path.Combine(dir.FullName, ".git");
        if (Directory.Exists(gitPath)) return true;   // real .git dir => main checkout
        if (File.Exists(gitPath)) return false;        // .git file (gitdir pointer) => linked worktree
    }
    return true; // not inside a git repo at all; keep the unscoped default
}
