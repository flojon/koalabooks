# Aspire Postgres Volume Isolation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Scope the Aspire Postgres data volume to the git worktree it's running from, so concurrent/sequential Aspire sessions in different worktrees never collide on the same DB state, while the main checkout keeps its existing volume name and data untouched.

**Architecture:** Single-file change to `src/KoalaBooks.AppHost/AppHost.cs`. A `[CallerFilePath]`-based local function computes the volume name at compile time from the AppHost source file's own directory: unchanged literal name in the main checkout, a short SHA-256 hash of the directory path as a suffix in any linked worktree, or an explicit `ASPIRE_DB_SUFFIX` env var override. No new files, no new packages, no automated tests (per spec's non-goals — this is a pure function with no I/O beyond one env var read).

**Tech Stack:** .NET 10 / C# top-level statements, .NET Aspire 13.4.6 AppHost SDK, `System.Security.Cryptography` (SHA-256), `System.Runtime.CompilerServices.CallerFilePathAttribute`.

## Global Constraints

- Main checkout's volume name MUST remain the exact literal `koalabooks-postgres-data` (spec: "Compatibility / migration cost" section — zero disruption to existing dev data).
- Main-checkout vs. worktree detection MUST use git's own `.git` file-vs-directory distinction, not the `.claude/worktrees/` path convention (spec: "Main-checkout detection").
- `ASPIRE_DB_SUFFIX`, when set and non-empty, MUST take precedence over both the main-checkout and hash-based paths (spec: "Override").
- The resolved volume name MUST be printed to console at AppHost startup (spec: "Discoverability").
- No automated test files — verify via a throwaway scratch harness, not a committed test project (spec: "Non-goals").

---

### Task 1: Implement per-worktree Postgres volume naming

**Files:**
- Modify: `src/KoalaBooks.AppHost/AppHost.cs`

**Interfaces:**
- Produces: `GetPostgresVolumeName()` — no parameters at call sites (the `sourcePath` parameter is `[CallerFilePath]`-supplied by the compiler, never passed explicitly); returns `string`.
- Produces: `IsMainCheckout(string startDir)` — returns `bool`; internal helper used only by `GetPostgresVolumeName`.

The current full contents of `AppHost.cs` are:

```csharp
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("koalabooks-postgres-data")
    .WithLifetime(ContainerLifetime.Persistent)
    .AddDatabase("koalabooks");

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web")
    .WithReference(postgres)
    .WaitFor(postgres);

builder.Build().Run();
```

- [ ] **Step 1: Replace `AppHost.cs` with the updated content**

Write the full file as:

```csharp
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
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build src/KoalaBooks.AppHost/KoalaBooks.AppHost.csproj`
Expected: `Build succeeded.` with 0 errors.

- [ ] **Step 3: Commit**

```bash
git add src/KoalaBooks.AppHost/AppHost.cs
git commit -m "$(cat <<'EOF'
Scope Aspire Postgres data volume per git worktree

Fixes #243: aspire start --isolated only randomizes ports and user
secrets, not container volumes, so two worktrees running Aspire
concurrently shared the same Postgres volume and silently
cross-contaminated each other's EF migration history. The volume
name now hashes the AppHost's own directory (stable per worktree,
identical for the main checkout regardless of branch) with an
ASPIRE_DB_SUFFIX env var escape hatch for manual override.
EOF
)"
```

---

### Task 2: Verify main-checkout / worktree / override classification

**Files:**
- Create (temporary, not committed): `$CLAUDE_JOB_DIR/tmp/volume-name-check/Program.cs`
- Create (temporary, not committed): `$CLAUDE_JOB_DIR/tmp/volume-name-check/volume-name-check.csproj`

**Interfaces:**
- Consumes: the exact `IsMainCheckout(string)` and hash logic from Task 1's `GetPostgresVolumeName` (copied into the scratch harness — this is throwaway verification code, not production code, so duplication here is fine and expected).

This task checks the pure classification/hash logic against real paths on disk — this repo's main checkout and two of its existing worktrees — without touching Docker or starting any Aspire session. This is deliberately not a committed test project (spec's "Non-goals": no automated tests); it's a disposable script, deleted at the end of this task.

- [ ] **Step 1: Write the scratch harness**

```bash
mkdir -p $CLAUDE_JOB_DIR/tmp/volume-name-check
cat > $CLAUDE_JOB_DIR/tmp/volume-name-check/volume-name-check.csproj <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>
</Project>
EOF
cat > $CLAUDE_JOB_DIR/tmp/volume-name-check/Program.cs <<'EOF'
using System.Security.Cryptography;
using System.Text;

string[] samples =
[
    "/home/flojon/koalabooks/src/KoalaBooks.AppHost",
    "/home/flojon/koalabooks/.claude/worktrees/aspire-postgres-volume-isolation-243/src/KoalaBooks.AppHost",
    "/home/flojon/koalabooks/.claude/worktrees/dialog-service-refactor/src/KoalaBooks.AppHost",
];

foreach (var appHostDir in samples)
{
    Console.WriteLine($"{appHostDir} => {GetPostgresVolumeName(appHostDir)}");
}

Console.WriteLine($"override => {GetPostgresVolumeName(samples[0], "scratch")}");

static string GetPostgresVolumeName(string appHostDir, string? overrideSuffix = null)
{
    const string baseName = "koalabooks-postgres-data";
    overrideSuffix ??= Environment.GetEnvironmentVariable("ASPIRE_DB_SUFFIX");

    if (!string.IsNullOrEmpty(overrideSuffix))
        return $"{baseName}-{overrideSuffix}";

    return IsMainCheckout(appHostDir)
        ? baseName
        : $"{baseName}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(appHostDir)))[..8].ToLowerInvariant()}";
}

static bool IsMainCheckout(string startDir)
{
    for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
    {
        var gitPath = Path.Combine(dir.FullName, ".git");
        if (Directory.Exists(gitPath)) return true;
        if (File.Exists(gitPath)) return false;
    }
    return true;
}
EOF
```

This mirrors Task 1's logic exactly (same three functions worth of behavior, collapsed into one testable `GetPostgresVolumeName(appHostDir, overrideSuffix)` so the override branch can be exercised directly without touching real environment variables or launching the actual AppHost).

- [ ] **Step 2: Run the harness**

Run: `dotnet run --project $CLAUDE_JOB_DIR/tmp/volume-name-check`

Expected output (four lines; exact hash suffixes will vary but must follow this shape):
```
/home/flojon/koalabooks/src/KoalaBooks.AppHost => koalabooks-postgres-data
/home/flojon/koalabooks/.claude/worktrees/aspire-postgres-volume-isolation-243/src/KoalaBooks.AppHost => koalabooks-postgres-data-<8 hex chars>
/home/flojon/koalabooks/.claude/worktrees/dialog-service-refactor/src/KoalaBooks.AppHost => koalabooks-postgres-data-<8 hex chars>
override => koalabooks-postgres-data-scratch
```
Confirm: line 1 has no hash suffix, lines 2 and 3 each have an 8-character lowercase hex suffix and the two suffixes differ from each other, and line 4 reads exactly `koalabooks-postgres-data-scratch`.

- [ ] **Step 3: Clean up the scratch harness**

```bash
rm -rf $CLAUDE_JOB_DIR/tmp/volume-name-check
```

No commit for this task — it produces no permanent repository changes, only console confirmation that Task 1's logic behaves correctly.

---

## After This Plan

The spec's own "Verification plan" section (steps 1–4) documents the full live Docker/Aspire end-to-end check — running real Aspire sessions from the main checkout and a worktree side by side and confirming two distinct volumes/containers in `docker volume ls`. That's a manual confirmation for whoever merges this, not automated here, since it involves starting long-lived `ContainerLifetime.Persistent` containers against the user's real dev environment.
