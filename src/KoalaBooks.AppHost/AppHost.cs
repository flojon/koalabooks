using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(GetPostgresVolumeName())
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("koalabooks");

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web")
    .WithReference(postgres)
    .WaitFor(postgres);

builder.Build().Run();

/// <summary>
/// Determines the Docker volume name for Postgres data, handling multiple dev environments.
///
/// WARNING: This function relies on [CallerFilePath] which embeds the absolute file path at compile time,
/// and then probes that same path on disk at runtime via IsMainCheckout(). This design assumes a normal
/// in-place developer build with no deterministic-build path remapping (PathMap, source-root substitution, etc.).
///
/// STALE BINARY CAVEAT: If the compiled AppHost binary is re-run after its git worktree has been deleted,
/// the path probe will fail to find .git markers at the (now-deleted) worktree path and will silently climb
/// the directory tree to the real repo root, resolving to the main checkout's volume name rather than erroring.
/// Rebuilding after deleting a worktree avoids this edge case.
/// </summary>
static string GetPostgresVolumeName([CallerFilePath] string sourcePath = "")
{
    const string baseName = "koalabooks-postgres-data";
    string volumeName;

    var overrideSuffix = Environment.GetEnvironmentVariable("ASPIRE_DB_SUFFIX");
    if (!string.IsNullOrEmpty(overrideSuffix))
    {
        var trimmedSuffix = overrideSuffix.Trim();
        ValidateSuffix(trimmedSuffix, baseName.Length);
        volumeName = $"{baseName}-{trimmedSuffix}";
    }
    else
    {
        var appHostDir = Path.GetDirectoryName(sourcePath);
        if (appHostDir == null)
        {
            // Unable to determine directory from caller path; fall back to main checkout name
            volumeName = baseName;
        }
        else
        {
            volumeName = IsMainCheckout(appHostDir)
                ? baseName
                : $"{baseName}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appHostDir)))[..8].ToLowerInvariant()}";
        }
    }

    Console.WriteLine($"[koalabooks] Postgres data volume: {volumeName}");
    return volumeName;
}

/// <summary>
/// Validates that a volume name suffix matches Docker volume naming requirements: [a-zA-Z0-9][a-zA-Z0-9_.-]*,
/// and that the resulting "{baseName}-{suffix}" volume name won't exceed Docker's 255-character limit.
/// </summary>
static void ValidateSuffix(string suffix, int baseNameLength)
{
    const int maxVolumeNameLength = 255;

    if (!Regex.IsMatch(suffix, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"))
    {
        throw new InvalidOperationException(
            $"ASPIRE_DB_SUFFIX value '{suffix}' is invalid. Docker volume names must match the pattern [a-zA-Z0-9][a-zA-Z0-9_.-]* " +
            $"(start with alphanumeric, contain only alphanumeric, underscore, period, or hyphen).");
    }

    var fullLength = baseNameLength + 1 + suffix.Length;
    if (fullLength > maxVolumeNameLength)
    {
        throw new InvalidOperationException(
            $"ASPIRE_DB_SUFFIX value '{suffix}' is too long: the resulting volume name would be {fullLength} characters, " +
            $"exceeding Docker's {maxVolumeNameLength}-character limit for volume names.");
    }
}

/// <summary>
/// Determines whether <paramref name="startDir"/> is inside the main git checkout, as opposed to a
/// linked worktree. Walks up from <paramref name="startDir"/> looking for a ".git" entry: a real
/// directory means the main checkout (git owns its .git folder directly), while a file there means a
/// linked worktree (git replaces .git with a "gitdir:" pointer back to the main repo's
/// .git/worktrees/&lt;name&gt;). If no .git marker is found at all, the path isn't inside a git repo,
/// so it defaults to "main checkout" to keep the unscoped volume name.
/// </summary>
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
