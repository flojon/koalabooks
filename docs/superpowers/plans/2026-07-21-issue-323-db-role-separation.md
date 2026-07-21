# DB Role Separation (Issue #323) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Introduce a non-superuser `app_user` Postgres role for the application's runtime queries, distinct from the existing `koalabooks` role which becomes migration/schema-owner-only, so that a future RLS layer (#163) can actually be enforced instead of silently bypassed.

**Architecture:** Keep `koalabooks` as the privileged role — it owns the schema, runs EF Core migrations at startup, and owns Hangfire's own job-storage schema (Hangfire needs DDL rights unrelated to tenant data, so it stays out of scope). Add a new `app_user` role with `SELECT/INSERT/UPDATE/DELETE` on all tables via `ALTER DEFAULT PRIVILEGES` (so it automatically covers every future migration's new tables with no extra grants needed) and no DDL rights. The app's DI-registered `AppDbContext` (used for all per-request EF Core queries, including OpenIddict and DataProtection key storage) is repointed at `app_user`; the one-time startup `MigrateAsync()` call is repointed at a separately-constructed `AppDbContext` using the `koalabooks` connection so it keeps working with a restricted runtime role in place.

**Tech Stack:** PostgreSQL 17 roles/grants, Docker Compose secrets + `docker-entrypoint-initdb.d` init scripts, .NET Aspire `Aspire.Hosting.PostgreSQL` (`WithInitFiles`, `AddParameter(secret: true)`, `ReferenceExpression`), Npgsql/EF Core connection strings.

## Global Constraints

- Role names: `koalabooks` (existing, privileged/migrator) and `app_user` (new, restricted runtime role) — exact names from issue #323.
- `app_user` must never be superuser and must own no tables (grants only, via `ALTER DEFAULT PRIVILEGES`).
- Existing 21 test call sites (across 15 files) of `PostgresContainerFixture.CreateUniqueDatabase()` must keep compiling and passing unchanged — this plan is additive to that fixture, not a rewrite.
- `docker-entrypoint-initdb.d` scripts (both Compose and Aspire's `WithInitFiles`) only run against a **brand-new, empty** data volume — they will not retroactively touch prod's or any developer's or any existing PR-preview's already-initialized volume. Every task that adds one must call this out.
- No secrets committed to git. Passwords flow only through Compose `secrets:`/`/run/secrets/*` files or Aspire secret parameters, never hardcoded in `.sql`/`.sh` files.

---

## File Structure

- `db-init/01-create-app-user.sh` (new) — idempotent shell script that creates the `app_user` role (reading its password from either a file path or a plain env var) and sets `ALTER DEFAULT PRIVILEGES` so every table/sequence the `koalabooks` role creates from then on (i.e. every EF migration) is auto-granted to `app_user`. Shared, unmodified, by prod compose, PR-preview compose, and Aspire local dev.
- `docker-compose.yml` (modify) — add `app_user_password` secret, mount `db-init/` into the `postgres` service's `/docker-entrypoint-initdb.d`, add `APP_USER_PASSWORD_FILE` env to `postgres`, add `ConnectionStrings__koalabooks_app` + `KOALABOOKS_APP_DB_PASSWORD_FILE` env to `web`.
- `docker-compose.pr-preview.yml` (modify) — same shape as above.
- `src/KoalaBooks.AppHost/AppHost.cs` (modify) — add a secret `app-user-password` parameter, mount `db-init/` via `WithInitFiles`, inject a manually-built `ConnectionStrings__koalabooks_app` env var into the web project using `ReferenceExpression`.
- `src/KoalaBooks.Web/Program.cs` (modify) — split into a `migratorConnectionString` (existing `koalabooks` connection, used for the startup migration and for Hangfire storage) and an `appConnectionString` (new `app_user` connection, used for `AddDbContext<AppDbContext>`/`EnrichNpgsqlDbContext`). The startup auto-migrate block builds its own throwaway `AppDbContext` against the migrator connection instead of resolving the DI-registered (now `app_user`-scoped) one.
- `src/KoalaBooks.Infrastructure/Data/DesignTimeDbContextFactory.cs` (modify) — comment-only: make explicit that `dotnet ef` tooling intentionally always runs as the privileged/migrator role.
- `tests/KoalaBooks.Tests/PostgresContainerFixture.cs` (modify) — create the `app_user` role once against the shared Testcontainers instance, add `CreateAppUserConnectionString(string dbName)` so future RLS tests (#163) can open a connection that isn't a superuser.
- `tests/KoalaBooks.Tests/PostgresContainerFixtureAppUserTests.cs` (new) — proves the new role exists, is not superuser, and can actually read/write through the default-privilege grants.

---

### Task 1: Shared Postgres init script for the `app_user` role

**Files:**
- Create: `db-init/01-create-app-user.sh`

**Interfaces:**
- Produces: a script invoked by Postgres's own `docker-entrypoint-initdb.d` mechanism at first container init. Reads the app_user password from `$APP_USER_PASSWORD_FILE` (Compose) if set, else falls back to `$APP_USER_PASSWORD` (Aspire). Runs as `$POSTGRES_USER` (i.e. `koalabooks`) against `$POSTGRES_DB`.

- [ ] **Step 1: Write the script**

```bash
#!/bin/bash
# Runs once, automatically, by the official postgres image's
# docker-entrypoint-initdb.d mechanism — ONLY on a brand-new, empty data
# volume. It will NOT run against prod's or any existing dev/preview volume;
# see the plan's "Manual Rollout" notes for those.
set -euo pipefail

if [ -n "${APP_USER_PASSWORD_FILE:-}" ]; then
    APP_USER_PASSWORD=$(cat "$APP_USER_PASSWORD_FILE")
fi

if [ -z "${APP_USER_PASSWORD:-}" ]; then
    # This is fatal, not a skip: docker-entrypoint.sh runs with `set -e`, so a
    # nonzero exit here aborts the whole container startup. That's intentional -
    # coming up without app_user would silently defeat the point of this role
    # separation, so we fail closed instead of booting Postgres without it.
    echo "01-create-app-user.sh: APP_USER_PASSWORD(_FILE) not set, aborting Postgres startup" >&2
    exit 1
fi

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" <<-EOSQL
    DO \$\$
    BEGIN
        IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'app_user') THEN
            CREATE ROLE app_user LOGIN PASSWORD '$APP_USER_PASSWORD';
        END IF;
    END
    \$\$;

    GRANT CONNECT ON DATABASE "$POSTGRES_DB" TO app_user;
    GRANT USAGE ON SCHEMA public TO app_user;

    -- Applies to tables/sequences that exist right now (there are none yet on a
    -- fresh volume) AND, via ALTER DEFAULT PRIVILEGES, to every table/sequence
    -- $POSTGRES_USER (the migrator role) creates from now on -- i.e. every future
    -- EF Core migration is automatically covered with no further grants needed.
    GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_user;
    GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
    ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO app_user;
EOSQL
```

- [ ] **Step 2: Make it executable**

Run: `chmod +x db-init/01-create-app-user.sh`

- [ ] **Step 3: Verify it against a throwaway container**

```bash
docker run --rm -d --name role-sep-test \
  -e POSTGRES_USER=koalabooks -e POSTGRES_PASSWORD=testpw -e POSTGRES_DB=koalabooks \
  -e APP_USER_PASSWORD=applocalpw \
  -v "$(pwd)/db-init:/docker-entrypoint-initdb.d:ro" \
  postgres:17-alpine
sleep 5
docker exec role-sep-test psql -U koalabooks -d koalabooks -c \
  "SELECT rolname, rolsuper FROM pg_roles WHERE rolname = 'app_user';"
```

Expected: one row, `app_user | f` (not superuser).

- [ ] **Step 4: Clean up**

Run: `docker stop role-sep-test`

- [ ] **Step 5: Commit**

```bash
git add db-init/01-create-app-user.sh
git commit -m "Add Postgres init script to provision non-superuser app_user role"
```

---

### Task 2: Wire the init script into `docker-compose.yml` (prod)

**Files:**
- Modify: `docker-compose.yml`

**Interfaces:**
- Consumes: `db-init/01-create-app-user.sh` from Task 1.
- Produces: `secrets/app_user_password` file convention (mirrors the existing `secrets/postgres_password` convention already used on the deploy VM — see reference memory `reference_pr_preview_infra`), `ConnectionStrings__koalabooks_app` env var consumed by `Program.cs` in Task 5.

- [ ] **Step 1: Add the new secret**

In the `secrets:` block at the bottom of `docker-compose.yml`, next to the existing `postgres_password` entry:

```yaml
secrets:
  # File must be owned by uid 1654 (the web image's non-root user) - Compose
  # bind-mounts it preserving host ownership and ignores mode/uid/gid outside Swarm.
  postgres_password:
    file: ./secrets/postgres_password
  app_user_password:
    file: ./secrets/app_user_password
```

- [ ] **Step 2: Mount the init script and pass the password into `postgres`**

```yaml
  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_USER: koalabooks
      POSTGRES_PASSWORD_FILE: /run/secrets/postgres_password
      POSTGRES_DB: koalabooks
      APP_USER_PASSWORD_FILE: /run/secrets/app_user_password
    secrets:
      - postgres_password
      - app_user_password
    volumes:
      - postgres-data:/var/lib/postgresql/data
      - ./db-init:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U koalabooks"]
      interval: 5s
      timeout: 5s
      retries: 5
    restart: unless-stopped
```

- [ ] **Step 3: Point the app at the restricted role**

```yaml
  web:
    image: ghcr.io/${GITHUB_REPOSITORY_OWNER:-local}/koalabooks-web:latest
    build:
      context: .
      dockerfile: src/KoalaBooks.Web/Dockerfile

    environment:
      - ConnectionStrings__koalabooks=Host=postgres;Port=5432;Database=koalabooks;Username=koalabooks
      - ConnectionStrings__koalabooks_app=Host=postgres;Port=5432;Database=koalabooks;Username=app_user
      - KOALABOOKS_DB_PASSWORD_FILE=/run/secrets/postgres_password
      - KOALABOOKS_APP_DB_PASSWORD_FILE=/run/secrets/app_user_password
      - ASPNETCORE_ENVIRONMENT=Production
      # Stopgap admin bootstrap until there's a real role-management UI.
      - AdminSeed__Email=jonas@floden.co
      - ASPNETCORE_URLS=http://+:8080
      - OTEL_EXPORTER_OTLP_ENDPOINT=http://aspire-dashboard:18889
      - OTEL_EXPORTER_OTLP_PROTOCOL=grpc
      # Path is "/", not "/signin-oidc" - the dashboard's own OIDC callback path is its root.
      - AspireDashboard__OidcRedirectUri=https://dashboard.koalasoft.se/
      - AspireDashboard__OidcClientSecret=${ASPIRE_DASHBOARD_OIDC_CLIENT_SECRET}
    secrets:
      - postgres_password
      - app_user_password
    depends_on:
      postgres:
        condition: service_healthy
      aspire-dashboard:
        condition: service_started
    restart: unless-stopped
```

- [ ] **Step 4: Validate the compose file parses**

Run: `docker compose -f docker-compose.yml config --quiet`
Expected: no output, exit code 0.

- [ ] **Step 5: Commit**

```bash
git add docker-compose.yml
git commit -m "Provision app_user role and secret in prod compose"
```

---

### Task 3: Wire the init script into `docker-compose.pr-preview.yml`

**Files:**
- Modify: `docker-compose.pr-preview.yml`

**Interfaces:**
- Consumes: `db-init/01-create-app-user.sh` from Task 1, same shape as Task 2.

- [ ] **Step 1: Apply the identical set of changes as Task 2** (secret, postgres env/volume/mount, web env)

```yaml
services:
  web:
    image: ghcr.io/__OWNER__/koalabooks-web:pr-__PR_NUMBER__
    environment:
      - ConnectionStrings__koalabooks=Host=postgres;Port=5432;Database=koalabooks;Username=koalabooks
      - ConnectionStrings__koalabooks_app=Host=postgres;Port=5432;Database=koalabooks;Username=app_user
      - KOALABOOKS_DB_PASSWORD_FILE=/run/secrets/postgres_password
      - KOALABOOKS_APP_DB_PASSWORD_FILE=/run/secrets/app_user_password
      - ASPNETCORE_ENVIRONMENT=Staging
      - ASPNETCORE_URLS=http://+:8080
      - SEED_DEMO_DATA=true
      # Matches DemoDataSeeder.DemoUserEmail, so the demo user it creates gets Admin.
      - AdminSeed__Email=admin@koalabooks.local
    secrets:
      - postgres_password
      - app_user_password
    depends_on:
      postgres:
        condition: service_healthy
    networks:
      - internal
      - pr-previews

  postgres:
    image: postgres:17-alpine
    environment:
      POSTGRES_USER: koalabooks
      POSTGRES_PASSWORD_FILE: /run/secrets/postgres_password
      POSTGRES_DB: koalabooks
      APP_USER_PASSWORD_FILE: /run/secrets/app_user_password
    secrets:
      - postgres_password
      - app_user_password
    volumes:
      - postgres-data:/var/lib/postgresql/data
      - ./db-init:/docker-entrypoint-initdb.d:ro
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U koalabooks"]
      interval: 5s
      timeout: 5s
      retries: 5
    networks:
      - internal

networks:
  internal:
  pr-previews:
    external: true

volumes:
  postgres-data:

secrets:
  # File must be owned by uid 1654 (the web image's non-root user) - Compose
  # bind-mounts it preserving host ownership and ignores mode/uid/gid outside Swarm.
  postgres_password:
    file: ./secrets/postgres_password
  app_user_password:
    file: ./secrets/app_user_password
```

- [ ] **Step 2: Check the PR-preview deploy workflow generates the new secret file**

Read `.github/workflows/pr-preview.yml` and confirm where it writes `secrets/postgres_password` per-PR (per reference memory `reference_pr_preview_infra`, it's generated via `openssl rand -hex 24`). Add an equivalent step writing `secrets/app_user_password` the same way, right next to the existing one, tracking its own `APP_USER_NEW_SECRET` flag (mirroring the existing `NEW_SECRET` flag but for this file).

**Also update the volume/password desync-sync block** (the `if [ "$NEW_SECRET" = true ] && [ "$VOLUME_EXISTS" = true ]` block that runs `ALTER USER koalabooks WITH PASSWORD ...`). This exact mechanism — a regenerated secret file paired with a pre-existing data volume/role — is what caused the incident tracked in `project_pr_preview_volume_password_desync_incident` (issue #195), and it applies equally to `app_user`: if the `app_user_password` file is ever regenerated (e.g. secrets directory wiped) while the volume/role persists, the role's actual DB password silently diverges from the new file and the app fails to connect. Extend that block (or add a parallel one gated on `APP_USER_NEW_SECRET && VOLUME_EXISTS`) to also run `ALTER USER app_user WITH PASSWORD '$(cat "$APP_USER_SECRET_FILE")';` in the same `psql` session, guarding the whole statement with `IF EXISTS`-style handling (or just accept it errors harmlessly) for the case where `app_user` doesn't exist yet on an old pre-#323 volume — that case is already covered by the Manual Rollout section's "recreate the preview" guidance, not by this sync block.

- [ ] **Step 3: Validate the compose file parses**

Run: `docker compose -f docker-compose.pr-preview.yml config --quiet`
Expected: no output, exit code 0 (may warn about `__OWNER__`/`__PR_NUMBER__` placeholders — that's expected, they're substituted by the workflow).

- [ ] **Step 4: Commit**

```bash
git add docker-compose.pr-preview.yml .github/workflows/pr-preview.yml
git commit -m "Provision app_user role and secret in PR-preview compose"
```

---

### Task 4: Wire the init script into local dev (`AppHost.cs`)

**Files:**
- Modify: `src/KoalaBooks.AppHost/AppHost.cs`

**Interfaces:**
- Consumes: `db-init/` directory from Task 1 (path relative to `src/KoalaBooks.AppHost/`, so `../../db-init`).
- Produces: `ConnectionStrings__koalabooks_app` env var injected into the `koalabooks-web` project, consumed by `Program.cs` in Task 5.

- [ ] **Step 1: Add a secret parameter, mount init files, and pass the password to the Postgres container**

```csharp
using KoalaBooks.AppHostSupport;

var builder = DistributedApplication.CreateBuilder(args);

var postgresVolumeName = VolumeNaming.GetVolumeName(Environment.GetEnvironmentVariable("ASPIRE_DB_SUFFIX"));
Console.WriteLine($"[koalabooks] Postgres data volume: {postgresVolumeName}");

var appUserPassword = builder.AddParameter("app-user-password", secret: true);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume(postgresVolumeName)
    .WithLifetime(ContainerLifetime.Persistent)
    .WithInitFiles("../../db-init")
    .WithEnvironment("APP_USER_PASSWORD", appUserPassword);

var koalabooksDb = postgres.AddDatabase("koalabooks");

builder.AddProject<Projects.KoalaBooks_Web>("koalabooks-web")
    .WithReference(koalabooksDb)
    .WithEnvironment(ctx =>
    {
        var endpoint = postgres.GetEndpoint("tcp");
        ctx.EnvironmentVariables["ConnectionStrings__koalabooks_app"] = ReferenceExpression.Create(
            $"Host={endpoint.Property(EndpointProperty.Host)};Port={endpoint.Property(EndpointProperty.Port)};Database=koalabooks;Username=app_user;Password={appUserPassword.Resource}");
    })
    .WaitFor(postgres);

builder.Build().Run();
```

- [ ] **Step 2: Build the AppHost project**

Run: `dotnet build src/KoalaBooks.AppHost`
Expected: build succeeds. If `WithInitFiles`, `WithEnvironment("APP_USER_PASSWORD", appUserPassword)`, or the `ReferenceExpression.Create` overload don't match this exact signature on the installed `Aspire.Hosting.PostgreSQL` version (13.4.6), the compiler error will name the mismatched overload — adjust to the closest matching one (e.g. `appUserPassword` may need no `.Resource` in the `WithEnvironment` call, or `ReferenceExpression` may need `endpoint.Property(EndpointProperty.Host)` swapped for `.Property(EndpointProperty.IPV4Host)`).

- [ ] **Step 3: Handle pre-existing local dev volumes**

The init script only runs on a brand-new volume. If your local Postgres data volume already exists (check with `docker volume ls | grep koalabooks-postgres-data`), either:
- delete it and let Aspire recreate it fresh: `docker volume rm koalabooks-postgres-data<your-suffix>`, or
- create `app_user` manually against the existing volume: `docker exec -it <postgres-container-name> psql -U koalabooks -d koalabooks` and paste the SQL body from `db-init/01-create-app-user.sh` (the `psql` heredoc contents) with a password of your choosing.

- [ ] **Step 4: Run the app and verify manually**

Run: `dotnet run --project src/KoalaBooks.AppHost` (or `aspire run` if using the Aspire CLI)

Once the Postgres container is healthy, in another terminal:
```bash
docker exec -it $(docker ps --filter name=postgres --format '{{.Names}}' | head -1) \
  psql -U koalabooks -d koalabooks -c "SELECT rolname, rolsuper FROM pg_roles WHERE rolname = 'app_user';"
```
Expected: `app_user | f`.

- [ ] **Step 5: Commit**

```bash
git add src/KoalaBooks.AppHost/AppHost.cs
git commit -m "Provision app_user role for local Aspire dev Postgres"
```

---

### Task 5: Split migrator vs. app connection strings in `Program.cs`

**Files:**
- Modify: `src/KoalaBooks.Web/Program.cs`
- Modify: `src/KoalaBooks.Infrastructure/Data/DesignTimeDbContextFactory.cs` (comment only)

**Interfaces:**
- Consumes: `ConnectionStrings:koalabooks` (existing, migrator), `ConnectionStrings:koalabooks_app` (new, from Tasks 2-4), `KOALABOOKS_DB_PASSWORD_FILE` (existing), `KOALABOOKS_APP_DB_PASSWORD_FILE` (new).
- Produces: `AppDbContext` registered in DI now connects as `app_user`; `migratorConnectionString` used for Hangfire storage and the one-off startup migration.

- [ ] **Step 1: Replace the single connection-string block**

In `src/KoalaBooks.Web/Program.cs`, replace lines 33-45:

```csharp
// Unpooled: AppDbContext's scoped ICurrentUser ctor dependency can't be resolved by a
// pooled context's activator, which only has access to the root provider.
var koalabooksConnectionString = builder.Configuration.GetConnectionString("koalabooks")!;
var dbPasswordFile = Environment.GetEnvironmentVariable("KOALABOOKS_DB_PASSWORD_FILE");
if (!string.IsNullOrEmpty(dbPasswordFile))
{
    koalabooksConnectionString = new NpgsqlConnectionStringBuilder(koalabooksConnectionString)
    {
        Password = File.ReadAllText(dbPasswordFile).Trim()
    }.ConnectionString;
}
builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(koalabooksConnectionString));
builder.EnrichNpgsqlDbContext<AppDbContext>();
```

with:

```csharp
// Unpooled: AppDbContext's scoped ICurrentUser ctor dependency can't be resolved by a
// pooled context's activator, which only has access to the root provider.
//
// Two roles: "koalabooks" is the privileged/migrator role that owns the schema and runs
// EF Core migrations plus Hangfire's own job-storage schema. "app_user" is a
// non-superuser, no-DDL role used for every request-scoped EF Core query, so that a
// future row-level-security layer has something to actually enforce against instead of
// being bypassed by a superuser connection.
var migratorConnectionString = builder.Configuration.GetConnectionString("koalabooks")!;
var migratorPasswordFile = Environment.GetEnvironmentVariable("KOALABOOKS_DB_PASSWORD_FILE");
if (!string.IsNullOrEmpty(migratorPasswordFile))
{
    migratorConnectionString = new NpgsqlConnectionStringBuilder(migratorConnectionString)
    {
        Password = File.ReadAllText(migratorPasswordFile).Trim()
    }.ConnectionString;
}

// Falls back to the migrator connection when koalabooks_app isn't configured, e.g. under
// the "Testing" environment's WebApplicationFactory harness, which only ever sets
// ConnectionStrings:koalabooks (see WebApiFactory.cs) and relies on EnsureCreated,
// not the restricted role.
var appConnectionString = builder.Configuration.GetConnectionString("koalabooks_app") ?? migratorConnectionString;
var appPasswordFile = Environment.GetEnvironmentVariable("KOALABOOKS_APP_DB_PASSWORD_FILE");
if (!string.IsNullOrEmpty(appPasswordFile))
{
    appConnectionString = new NpgsqlConnectionStringBuilder(appConnectionString)
    {
        Password = File.ReadAllText(appPasswordFile).Trim()
    }.ConnectionString;
}

builder.Services.AddDbContext<AppDbContext>(options => options.UseNpgsql(appConnectionString));
builder.EnrichNpgsqlDbContext<AppDbContext>();
```

- [ ] **Step 2: Point Hangfire at the migrator connection**

Replace (around the current line 59):
```csharp
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(koalabooksConnectionString)));
```
with:
```csharp
        .UsePostgreSqlStorage(options => options.UseNpgsqlConnection(migratorConnectionString)));
```

- [ ] **Step 3: Point the startup auto-migrate at the migrator connection**

Replace the auto-migrate block (around current lines 257-278):

```csharp
// Auto-migrate and seed on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Environment.IsEnvironment("Testing"))
    {
        db.Database.EnsureCreated();
    }
    else
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                break;
            }
            catch (Exception) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
```

with:

```csharp
// Auto-migrate and seed on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    if (app.Environment.IsEnvironment("Testing"))
    {
        db.Database.EnsureCreated();
    }
    else
    {
        // Migrations need DDL rights app_user intentionally doesn't have, so this runs
        // against a separate, throwaway AppDbContext built on the migrator connection
        // rather than the DI-registered (app_user-scoped) one resolved above.
        var migratorOptions = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(migratorConnectionString)
            .Options;
        await using var migratorDb = new AppDbContext(migratorOptions, new LocalCurrentUser());

        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                await migratorDb.Database.MigrateAsync();
                break;
            }
            catch (Exception) when (attempt < 9)
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
        }
```

Note: `LocalCurrentUser` is `KoalaBooks.Domain.LocalCurrentUser` — check the existing `using` directives at the top of `Program.cs` and add `using KoalaBooks.Domain;` if it isn't already present (confirm with `grep -n "^using KoalaBooks.Domain;" src/KoalaBooks.Web/Program.cs`).

The `db` variable resolved from DI a few lines above is now used only for the `EnsureCreated()` (Testing) branch and any code after this block (e.g. `DemoDataSeeder.SeedAsync(scope.ServiceProvider)`, which resolves its own scoped `AppDbContext` from the service provider) — leave those untouched.

- [ ] **Step 4: Update the design-time factory's comment**

In `src/KoalaBooks.Infrastructure/Data/DesignTimeDbContextFactory.cs`, add a one-line comment above the connection string so it's clear this is intentional, not an oversight:

```csharp
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        // dotnet ef tooling always runs as the privileged/migrator role - it needs DDL
        // rights the runtime app_user role intentionally doesn't have.
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseNpgsql("Host=localhost;Database=KoalaBooks;Username=postgres;Password=postgres");
        return new AppDbContext(optionsBuilder.Options, new LocalCurrentUser());
    }
}
```

- [ ] **Step 5: Build**

Run: `dotnet build src/KoalaBooks.Web`
Expected: build succeeds with no errors.

- [ ] **Step 6: Run the full test suite**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all existing tests still pass (this confirms the `?? migratorConnectionString` fallback didn't change behavior for `WebApplicationFactory`-based tests).

- [ ] **Step 7: Manual end-to-end check against Compose**

```bash
mkdir -p secrets
openssl rand -hex 24 > secrets/postgres_password
openssl rand -hex 24 > secrets/app_user_password
docker compose up -d --build
sleep 15
docker compose exec postgres psql -U koalabooks -d koalabooks -c \
  "SELECT rolname, rolsuper FROM pg_roles WHERE rolname = 'app_user';"
docker compose logs web --tail 50
```
Expected: `app_user | f` row present; web logs show migrations applied and the app serving requests without connection errors (confirms the app_user-scoped runtime connection can actually read/write through the `ALTER DEFAULT PRIVILEGES` grants against tables the migrator just created).

```bash
docker compose down -v
```

- [ ] **Step 8: Commit**

```bash
git add src/KoalaBooks.Web/Program.cs src/KoalaBooks.Infrastructure/Data/DesignTimeDbContextFactory.cs
git commit -m "Split migrator and app_user connections in Program.cs"
```

---

### Task 6: Non-superuser connection option in `PostgresContainerFixture`

**Files:**
- Modify: `tests/KoalaBooks.Tests/PostgresContainerFixture.cs`
- Create: `tests/KoalaBooks.Tests/PostgresContainerFixtureAppUserTests.cs`

**Interfaces:**
- Consumes: nothing new — same Testcontainers `PostgreSqlContainer` already in the fixture.
- Produces: `PostgresContainerFixture.CreateAppUserConnectionString(string dbName)`, callable by any test (including future #163 RLS tests) that needs a non-superuser connection to a database created via the existing `CreateUniqueDatabase()`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/KoalaBooks.Tests/PostgresContainerFixtureAppUserTests.cs
using Npgsql;
using Xunit;

namespace KoalaBooks.Tests;

public class PostgresContainerFixtureAppUserTests
{
    [Fact]
    public void AppUserConnection_IsNotSuperuser()
    {
        var (dbName, _) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            var appConnStr = PostgresContainerFixture.CreateAppUserConnectionString(dbName);

            using var conn = new NpgsqlConnection(appConnStr);
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT rolsuper FROM pg_roles WHERE rolname = current_user;";
            var isSuperuser = (bool)cmd.ExecuteScalar()!;

            Assert.False(isSuperuser);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }

    [Fact]
    public void AppUserConnection_CanReadWriteTablesCreatedByMigratorRole()
    {
        var (dbName, migratorConnStr) = PostgresContainerFixture.CreateUniqueDatabase();
        try
        {
            using (var migratorConn = new NpgsqlConnection(migratorConnStr))
            {
                migratorConn.Open();
                using var createCmd = migratorConn.CreateCommand();
                createCmd.CommandText = "CREATE TABLE role_sep_probe (id serial primary key, value text);";
                createCmd.ExecuteNonQuery();
            }

            var appConnStr = PostgresContainerFixture.CreateAppUserConnectionString(dbName);
            using var appConn = new NpgsqlConnection(appConnStr);
            appConn.Open();

            using var insertCmd = appConn.CreateCommand();
            insertCmd.CommandText = "INSERT INTO role_sep_probe (value) VALUES ('ok');";
            insertCmd.ExecuteNonQuery();

            using var selectCmd = appConn.CreateCommand();
            selectCmd.CommandText = "SELECT value FROM role_sep_probe;";
            var value = (string)selectCmd.ExecuteScalar()!;

            Assert.Equal("ok", value);
        }
        finally
        {
            PostgresContainerFixture.DropDatabase(dbName);
        }
    }
}
```

- [ ] **Step 2: Run it to verify it fails to compile**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~PostgresContainerFixtureAppUserTests"`
Expected: build error — `CreateAppUserConnectionString` doesn't exist yet.

- [ ] **Step 3: Implement the role creation and connection helper**

Replace `tests/KoalaBooks.Tests/PostgresContainerFixture.cs` in full:

```csharp
using Npgsql;
using Testcontainers.PostgreSql;

namespace KoalaBooks.Tests;

/// <summary>
/// One Postgres container per test process, shared across all test classes.
/// Each caller gets its own database via CreateUniqueDatabase() so test classes
/// can run in parallel without interfering with each other.
/// </summary>
internal static class PostgresContainerFixture
{
    private const string AppUserPassword = "test-app-user-password";

    private static readonly PostgreSqlContainer _container = CreateAndStart();

    private static PostgreSqlContainer CreateAndStart()
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        container.StartAsync().GetAwaiter().GetResult();
        CreateAppUserRole(container);
        return container;
    }

    private static void CreateAppUserRole(PostgreSqlContainer container)
    {
        using var conn = new NpgsqlConnection(container.GetConnectionString());
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'app_user') THEN
                    CREATE ROLE app_user LOGIN PASSWORD '{AppUserPassword}';
                END IF;
            END
            $$;
            """;
        cmd.ExecuteNonQuery();
    }

    public static string ConnectionString => _container.GetConnectionString();

    public static (string dbName, string connStr) CreateUniqueDatabase()
    {
        var dbName = $"koalatest_{Guid.NewGuid():N}";
        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"CREATE DATABASE \"{dbName}\"";
        cmd.ExecuteNonQuery();

        GrantAppUserOnDatabase(dbName);

        var connStr = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = dbName }.ConnectionString;
        return (dbName, connStr);
    }

    private static void GrantAppUserOnDatabase(string dbName)
    {
        var connStrForNewDb = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = dbName }.ConnectionString;
        using var conn = new NpgsqlConnection(connStrForNewDb);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            GRANT USAGE ON SCHEMA public TO app_user;
            GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA public TO app_user;
            GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA public TO app_user;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO app_user;
            ALTER DEFAULT PRIVILEGES IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO app_user;
            """;
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Non-superuser connection to a database created via CreateUniqueDatabase(), distinct
    /// from the superuser connection used for schema setup. Lets row-level-security tests
    /// verify enforcement actually happens instead of being silently bypassed by a superuser
    /// session.
    /// </summary>
    public static string CreateAppUserConnectionString(string dbName)
    {
        return new NpgsqlConnectionStringBuilder(ConnectionString)
        {
            Database = dbName,
            Username = "app_user",
            Password = AppUserPassword
        }.ConnectionString;
    }

    public static void DropDatabase(string dbName)
    {
        using var conn = new NpgsqlConnection(ConnectionString);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"DROP DATABASE IF EXISTS \"{dbName}\" WITH (FORCE)";
        cmd.ExecuteNonQuery();
    }
}
```

Note: `GrantAppUserOnDatabase` runs the same grants Task 1's init script sets up for Compose/Aspire, but scoped per-test-database instead of per-cluster, since Testcontainers gives every test its own database (Task 1's `ALTER DEFAULT PRIVILEGES` is cluster/role-scoped and doesn't cross databases).

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test tests/KoalaBooks.Tests --filter "FullyQualifiedName~PostgresContainerFixtureAppUserTests"`
Expected: both tests PASS.

- [ ] **Step 5: Run the full test suite to confirm no regressions**

Run: `dotnet test tests/KoalaBooks.Tests`
Expected: all tests pass (the 21 existing `CreateUniqueDatabase()` call sites are unaffected since that method's signature and superuser behavior didn't change).

- [ ] **Step 6: Commit**

```bash
git add tests/KoalaBooks.Tests/PostgresContainerFixture.cs tests/KoalaBooks.Tests/PostgresContainerFixtureAppUserTests.cs
git commit -m "Expose non-superuser app_user connection option in PostgresContainerFixture"
```

---

## Manual Rollout (not part of automated task execution)

`docker-entrypoint-initdb.d` scripts (Tasks 1-4) only run against a **brand-new, empty** Postgres data volume. They will silently do nothing on any volume that already exists — this is the exact mechanism that caused the PR-preview 502 incident tracked in project memory `project_pr_preview_volume_password_desync_incident` (issue #195): a regenerated secret combined with a pre-existing volume that never re-read it.

Before this ships to an environment with a pre-existing volume, a human needs to run the `app_user` creation SQL from `db-init/01-create-app-user.sh` manually against that volume:

- **Prod** (`oraclevm`, `/opt/koalabooks/`): SSH in, generate a password with `openssl rand -hex 24 > secrets/app_user_password`, then run the script's SQL body via `docker compose exec postgres psql -U koalabooks -d koalabooks` before redeploying with the new compose file — mirrors the precedent already used for the prod `POSTGRES_PASSWORD_FILE` cutover in PR #182.
- **Existing PR previews**: per `reference_pr_preview_infra`, preview data is disposable — `docker compose -p pr-<n> down -v && up -d` (with `SEED_DEMO_DATA=true`) is simpler than manual role creation and matches how the earlier volume/password desync incident was resolved. Confirm with the user before running this against a live preview, same as last time.
- **Existing local dev volumes**: covered already in Task 4, Step 3.

This plan does not automate any of the above — they touch shared/production state and should be executed deliberately, not as an unattended task in a subagent loop.
