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
