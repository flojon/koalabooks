# PR Preview Deployments

**Date:** 2026-06-13
**Status:** Approved

## Overview

Each open PR gets its own isolated deployment on the production VM at `pr-{n}.books.koalasoft.se`. Each environment runs a dedicated web container and a fresh PostgreSQL database. On PR close the environment and its data are wiped. Production is unaffected.

## Architecture

```
GitHub PR open/sync
  → CI builds + pushes image tagged pr-{n} to GHCR
  → SSH: write compose file + Caddy snippet
  → docker compose -p pr-{n} up -d
  → Caddy reload → pr-{n}.books.koalasoft.se is live

GitHub PR close
  → SSH: docker compose -p pr-{n} down -v
  → Remove Caddy snippet, Caddy reload
  → Delete image from VM and from GHCR
```

Production runs in the existing `koalabooks` Compose project and is never touched by PR workflows.

## DNS & Routing

- **DNS**: Wildcard A record `*.books.koalasoft.se → VM IP` added in DirectAdmin for `koalasoft.se`. Existing `books.koalasoft.se` record unchanged.
- **TLS**: Caddy obtains individual ACME certs per PR subdomain automatically (HTTP-01 challenge).
- **Caddyfile**: `import /etc/caddy/snippets/*.caddy` at the top. Per-PR snippet files are mounted from `/opt/koalabooks/caddy-snippets/` into the Caddy container at `/etc/caddy/snippets/`.
- **Shared network**: A Docker external network `pr-previews` connects the Caddy container to all PR web containers. No host ports are exposed.

Per-PR Caddy snippet (`caddy-snippets/pr-{n}.caddy`):
```
pr-{n}.books.koalasoft.se {
    reverse_proxy pr-{n}-web-1:8080
}
```

## CI/CD Workflows

### Changes to `ci.yml`

No changes needed. `ci.yml` continues to skip pushing images for PRs. The `pr-preview.yml` workflow handles its own build and push.

### New `pr-preview.yml`

Triggered on `pull_request` events (opened, synchronize, reopened, closed) against `main`.
Permissions: `contents: read`, `packages: write`, `pull-requests: write`.

**`deploy` job** (skips when action is `closed`):
1. Build and push image to GHCR tagged `pr-{n}` (reuses registry layer cache from `ci.yml` builds).
2. SSH into VM.
3. Write `/opt/koalabooks/pr-{n}/docker-compose.yml` from template.
4. Write `/opt/koalabooks/caddy-snippets/pr-{n}.caddy`.
5. `docker compose -p pr-{n} up -d`.
6. `docker exec koalabooks-caddy-1 caddy reload --config /etc/caddy/Caddyfile`.
7. Post PR comment containing `<!-- pr-preview -->` as a hidden marker, with URL `https://pr-{n}.books.koalasoft.se`.

**`cleanup` job** (only when action is `closed`):
1. SSH into VM.
2. `docker compose -p pr-{n} down -v`.
3. `docker rmi ghcr.io/${{ github.repository_owner }}/koalabooks-web:pr-{n}` (ignore error if already gone).
4. Remove `/opt/koalabooks/caddy-snippets/pr-{n}.caddy`.
5. Remove `/opt/koalabooks/pr-{n}/` directory.
6. `docker exec koalabooks-caddy-1 caddy reload --config /etc/caddy/Caddyfile`.
7. Delete the `pr-{n}` package version from GHCR via GitHub API.
8. Find and delete the PR comment by searching for the `<!-- pr-preview -->` marker via GitHub API.

### New `pr-preview-cleanup.yml` (weekly cron)

Runs weekly. Queries GHCR for all `pr-*` tagged images, cross-references open PRs via GitHub API, and deletes any images whose PR is no longer open. Also prunes orphaned directories under `/opt/koalabooks/` and stale Caddy snippets on the VM.

## VM Setup (one-time)

1. `docker network create pr-previews`
2. Update `docker-compose.yml`:
   - Add `pr-previews` external network to the `caddy` service.
   - Mount `./caddy-snippets:/etc/caddy/snippets` in the Caddy service.
   - Declare `pr-previews` as an external network at the top level.
3. Update `Caddyfile`: add `import /etc/caddy/snippets/*.caddy`.
4. Create `/opt/koalabooks/caddy-snippets/` directory.
5. `docker compose up -d caddy` to apply the volume and network changes.

## Per-PR Docker Compose Template

Written to `/opt/koalabooks/pr-{n}/docker-compose.yml` by the deploy job:

```yaml
services:
  web:
    image: ghcr.io/${{ github.repository_owner }}/koalabooks-web:pr-{n}  # substituted at deploy time
    environment:
      - ConnectionStrings__koalabooks=Host=postgres;Port=5432;Database=koalabooks;Username=koalabooks;Password=${POSTGRES_PASSWORD}
      - ASPNETCORE_ENVIRONMENT=Staging
      - ASPNETCORE_URLS=http://+:8080
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
      POSTGRES_PASSWORD: ${POSTGRES_PASSWORD}
      POSTGRES_DB: koalabooks
    volumes:
      - postgres-data:/var/lib/postgresql/data
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
```

`POSTGRES_PASSWORD` for PR envs uses a dedicated GitHub secret (`PR_POSTGRES_PASSWORD`), separate from production.

## Secrets Required

| Secret | Already exists | Notes |
|---|---|---|
| `DEPLOY_HOST` | Yes | Shared with prod deploy |
| `DEPLOY_USER` | Yes | Shared with prod deploy |
| `DEPLOY_SSH_KEY` | Yes | Shared with prod deploy |
| `PR_POSTGRES_PASSWORD` | No | Fixed password for ephemeral PR databases |

## Constraints & Notes

- Caddy container name must be predictable (`koalabooks-caddy-1` by default with Compose project `koalabooks`) — the reload command targets it by name.
- PR web containers are named `pr-{n}-web-1` by Docker Compose convention; the Caddy snippet uses this name directly.
- No resource limits are set on PR containers. If contention becomes an issue, `mem_limit` can be added to the template.
- The Aspire dashboard is not included in PR environments — production's dashboard is sufficient for observability.
