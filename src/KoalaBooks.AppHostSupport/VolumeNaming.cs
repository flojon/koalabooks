using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace KoalaBooks.AppHostSupport;

/// <summary>
/// Computes the Docker volume name used for the Postgres data volume, scoped per git checkout
/// so that concurrent Aspire sessions in different worktrees never collide on the same volume.
/// Pure logic only — no environment or console I/O; callers own reading ASPIRE_DB_SUFFIX and
/// printing the resolved name. Lives in its own library (rather than in KoalaBooks.AppHost itself)
/// so it can be referenced from tests without pulling in AppHost's top-level-statement Program type,
/// which would collide with KoalaBooks.Web's Program type in the test assembly.
/// </summary>
public static class VolumeNaming
{
    public const string BaseName = "koalabooks-postgres-data";
    private const int MaxVolumeNameLength = 255;

    /// <summary>
    /// Determines the Docker volume name for Postgres data, handling multiple dev environments.
    ///
    /// WARNING: This function relies on [CallerFilePath] which embeds the absolute file path of its
    /// call site at compile time, and then probes that same path on disk at runtime via
    /// IsMainCheckout(). This design assumes a normal in-place developer build with no
    /// deterministic-build path remapping (PathMap, source-root substitution, etc.).
    ///
    /// STALE BINARY CAVEAT: If the compiled AppHost binary is re-run after its git worktree has been
    /// deleted, the path probe will fail to find .git markers at the (now-deleted) worktree path and
    /// will silently climb the directory tree to the real repo root, resolving to the main checkout's
    /// volume name rather than erroring. Rebuilding after deleting a worktree avoids this edge case.
    /// </summary>
    public static string GetVolumeName(string? overrideSuffix, [CallerFilePath] string sourcePath = "")
    {
        if (!string.IsNullOrEmpty(overrideSuffix))
        {
            var trimmedSuffix = overrideSuffix.Trim();
            ValidateSuffix(trimmedSuffix, BaseName.Length);
            return $"{BaseName}-{trimmedSuffix}";
        }

        var appHostDir = Path.GetDirectoryName(sourcePath);
        if (appHostDir is null)
        {
            // Unable to determine directory from caller path; fall back to main checkout name
            return BaseName;
        }

        return IsMainCheckout(appHostDir)
            ? BaseName
            : $"{BaseName}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appHostDir)))[..8].ToLowerInvariant()}";
    }

    /// <summary>
    /// Validates that a volume name suffix matches Docker volume naming requirements: [a-zA-Z0-9][a-zA-Z0-9_.-]*,
    /// and that the resulting "{baseName}-{suffix}" volume name won't exceed Docker's 255-character limit.
    /// </summary>
    public static void ValidateSuffix(string suffix, int baseNameLength)
    {
        if (!Regex.IsMatch(suffix, @"^[a-zA-Z0-9][a-zA-Z0-9_.-]*$"))
        {
            throw new InvalidOperationException(
                $"ASPIRE_DB_SUFFIX value '{suffix}' is invalid. Docker volume names must match the pattern [a-zA-Z0-9][a-zA-Z0-9_.-]* " +
                $"(start with alphanumeric, contain only alphanumeric, underscore, period, or hyphen).");
        }

        var fullLength = baseNameLength + 1 + suffix.Length;
        if (fullLength > MaxVolumeNameLength)
        {
            throw new InvalidOperationException(
                $"ASPIRE_DB_SUFFIX value '{suffix}' is too long: the resulting volume name would be {fullLength} characters, " +
                $"exceeding Docker's {MaxVolumeNameLength}-character limit for volume names.");
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
    public static bool IsMainCheckout(string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            var gitPath = Path.Combine(dir.FullName, ".git");
            if (Directory.Exists(gitPath)) return true;   // real .git dir => main checkout
            if (File.Exists(gitPath)) return false;        // .git file (gitdir pointer) => linked worktree
        }
        return true; // not inside a git repo at all; keep the unscoped default
    }
}
