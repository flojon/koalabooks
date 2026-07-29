# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

```bash
# Run the app (Aspire orchestrates Postgres + the Web project)
aspire start                       # see .claude/skills/aspire/SKILL.md for the full CLI
aspire start --isolated            # in a worktree, to avoid port/volume conflicts

# Run all tests (integration tests spin up real Postgres via Testcontainers - Docker must be running)
dotnet test

# Run a single test / fixture
dotnet test --filter "FullyQualifiedName~BookkeepingTests"
dotnet test --filter "DisplayName~SomeSpecificTest"

# EF Core migrations (Infrastructure holds migrations, Web is the startup project)
dotnet ef migrations add <Name> --project src/KoalaBooks.Infrastructure --startup-project src/KoalaBooks.Web
dotnet ef database update --project src/KoalaBooks.Infrastructure --startup-project src/KoalaBooks.Web
```

There is no lint/format command configured yet.

## Workflow

- Before starting new work, `git fetch origin` and work in a worktree — don't branch off a possibly-stale local `main`.
- When finishing a branch, always push and open a PR — never merge locally.
- Code comments: keep them minimal and concise, and never reference issue/PR numbers or ticket context (put that in the commit message / PR description instead).

## Debugging PR previews

Previews run at `pr-<n>.books.koalasoft.se` on the same VM as prod, reachable via SSH alias `oraclevm`. Deploy state is at `/opt/koalabooks/pr-<n>/` (per-PR `docker-compose.yml`, Caddy snippet, and a `secrets/postgres_password` file — no shared Postgres password secret anymore). If a preview 502s, check there first:

```bash
ssh oraclevm 'docker ps -a --filter name=pr-<n>'
ssh oraclevm 'docker logs pr-<n>-web-1'
ssh oraclevm 'docker logs pr-<n>-postgres-1'
```
