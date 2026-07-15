# Aspire local dev: scope the Postgres data volume per worktree (#243)

## Background

`src/KoalaBooks.AppHost/AppHost.cs` provisions Postgres with a hardcoded,
literal data volume name:

```csharp
.AddPostgres("postgres")
    .WithDataVolume("koalabooks-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent)
```

`aspire start/run --isolated` only randomizes ports and isolates user
secrets — it does not scope container resources, networks, or volumes.
Two Aspire sessions running from two different git worktrees (or a
worktree and the main checkout) mount the exact same named Docker volume,
so `__EFMigrationsHistory` and schema state from one branch silently leak
into another branch's session. This caused a real failure during #208's
verification (see issue #243 for the full incident writeup).

## Goal

Give every git worktree its own Postgres data volume, automatically, with
no change to the developer's day-to-day workflow — while keeping the
main checkout's volume name and data exactly as they are today, since
branch-switching there is expected to share one continuous scratch database.

## Non-goals

- Not solving collisions from manually switching between schema-divergent
  branches inside the *same* checkout (main checkout or a single worktree).
  That's pre-existing behavior, self-diagnosable with the same
  `docker volume rm` recovery already documented in #243, and not something
  worth trading main-checkout data continuity for.
- Not touching production topology (`docker-compose.yml`) — this is
  Aspire local-dev-only.
- No automated tests — this is a single pure naming function with no I/O
  beyond one environment variable read; verification is manual.

## Design

### Volume naming function

Add a local function in `AppHost.cs`:

```csharp
static string GetPostgresVolumeName([CallerFilePath] string sourcePath = "")
{
    const string baseName = "koalabooks-postgres-data";

    var overrideSuffix = Environment.GetEnvironmentVariable("ASPIRE_DB_SUFFIX");
    if (!string.IsNullOrEmpty(overrideSuffix))
        return $"{baseName}-{overrideSuffix}";

    var appHostDir = Path.GetDirectoryName(sourcePath)!;
    if (IsMainCheckout(appHostDir))
        return baseName;

    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appHostDir)))[..8].ToLowerInvariant();
    return $"{baseName}-{hash}";
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
```

Call site becomes:

```csharp
var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(GetPostgresVolumeName())
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("koalabooks");
```

### Why `[CallerFilePath]`

It bakes in the absolute path of `AppHost.cs` at compile time. Since each
git worktree is a full separate checkout on disk, that path is naturally
different per worktree, and identical every time the project is built from
the main checkout — regardless of which branch happens to be checked out
there. No git subprocess calls, no reliance on the AppHost process's
runtime working directory (which can vary depending on how `aspire`/`dotnet
run` is invoked).

### Main-checkout detection

Walk up from the AppHost's directory looking for `.git`. In the main
checkout it's a real directory; in any linked worktree, git replaces it
with a file containing a `gitdir:` pointer back to the main repo's
`.git/worktrees/<name>`. This is git's own mechanism for distinguishing
the two, so it's not tied to the `.claude/worktrees/` naming convention —
it works for any worktree, however it was created.

### Override

Setting `ASPIRE_DB_SUFFIX` before `aspire start`/`aspire run` forces a
specific suffix, bypassing both the main-checkout and hash paths. Intended
use: spinning up a disposable scratch DB for migration testing without
touching the shared main-checkout data.

### Discoverability

Add one `Console.WriteLine` at AppHost startup logging the resolved volume
name. #243's root complaint was that the collision was invisible until a
cryptic Postgres error surfaced through the UI — printing the name makes
`docker volume ls` immediately actionable if something looks wrong.

### Compatibility / migration cost

None. The main checkout keeps the literal `koalabooks-postgres-data` name
it has always had, so existing data there is unaffected. Only worktrees —
which today already collide and have no persistent identity worth
preserving — get new (previously nonexistent) isolated volumes.

## Verification plan

1. From the main checkout, run `aspire start` (or `run`), confirm the
   printed volume name is exactly `koalabooks-postgres-data` and matches
   pre-existing `docker volume ls` output.
2. From a second worktree (e.g. this one), run `aspire start`, confirm the
   printed volume name is `koalabooks-postgres-data-<8 hex chars>` and that
   `docker volume ls` now shows both volumes mounted by two different,
   concurrently-running Postgres containers.
3. Set `ASPIRE_DB_SUFFIX=scratch` in a worktree, run `aspire start`, confirm
   the volume is named `koalabooks-postgres-data-scratch`.
4. Re-run from the same worktree without the env var, confirm it reverts to
   the hash-based name and the container mounts the volume from step 2
   (data persists across restarts of the same worktree, as expected from
   `ContainerLifetime.Persistent` + `WithDataVolume`).
