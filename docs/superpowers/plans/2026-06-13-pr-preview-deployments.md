# PR Preview Deployments Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deploy each PR to an isolated environment at `pr-{n}.books.koalasoft.se` and tear it down automatically when the PR closes.

**Architecture:** Each PR runs its own Docker Compose project (`-p pr-{n}`) on the production VM with a dedicated web container and a fresh PostgreSQL database. A shared Docker external network (`pr-previews`) connects all PR web containers to the existing Caddy container, which picks up per-PR routing by importing Caddy snippet files from a mounted directory. Two new GitHub Actions workflows handle deploy (on PR open/sync) and cleanup (on PR close), plus a weekly cron for orphan cleanup.

**Tech Stack:** GitHub Actions, Docker Compose, Caddy 2, GHCR (ghcr.io), `appleboy/ssh-action@v1`, `appleboy/scp-action@v1.0.0`, `actions/github-script@v7`, `docker/build-push-action@v7`

---

> ⚠️ **Ordering constraint:** Task 1 (VM setup) must be completed and Task 3 (docker-compose.yml + Caddyfile changes) must be merged to `main` BEFORE opening any PR to test previews. Task 3 changes are automatically applied to the VM by the existing `deploy.yml` workflow when they reach `main`. The VM network and directory created in Task 1 must already exist before Caddy restarts with the new volume mount.
>
> ⚠️ **Caddy container name:** The plan assumes the production Compose project is named `koalabooks` (the default when running `docker compose` from `/opt/koalabooks`), which makes the Caddy container name `koalabooks-caddy-1`. Verify with `docker ps --filter name=caddy` on the VM before proceeding.
>
> ⚠️ **GHCR API — user vs org:** The GHCR package deletion steps use the `...ForPackageOwnedByUser` API, which is correct for personal GitHub accounts. If the repo is under a GitHub organisation, change every `getAllPackageVersionsForPackageOwnedByUser` / `deletePackageVersionForUser` call to the `...ForPackageOwnedByOrg` variant and replace `username:` with `org:`.

---

### Task 1: VM one-time setup (manual — do this first)

SSH into the VM and run these commands. They are prerequisites for all subsequent tasks.

**Files:** none (manual steps on the server)

- [ ] **Step 1: Create the shared Docker network**

```bash
docker network create pr-previews
```

Expected: prints a 64-character hex network ID, e.g. `a1b2c3d4e5f6...`

- [ ] **Step 2: Create the Caddy snippets directory**

```bash
mkdir -p /opt/koalabooks/caddy-snippets
```

- [ ] **Step 3: Add the `PR_POSTGRES_PASSWORD` GitHub secret**

Go to: GitHub repo → Settings → Secrets and variables → Actions → New repository secret.

- **Name:** `PR_POSTGRES_PASSWORD`
- **Value:** any strong random password (this is for ephemeral PR databases only, not production — data is wiped on PR close)

---

### Task 2: Add the per-PR Docker Compose template

This template is stored in the repo. The deploy workflow copies it to the VM and uses `sed` to substitute PR-specific values at deploy time. Placeholders use `__UNDERSCORES__` to avoid conflicts with shell or YAML syntax.

**Files:**
- Create: `docker-compose.pr-preview.yml`

- [ ] **Step 1: Create `docker-compose.pr-preview.yml` at the repo root**

```yaml
services:
  web:
    image: ghcr.io/__OWNER__/koalabooks-web:pr-__PR_NUMBER__
    environment:
      - ConnectionStrings__koalabooks=Host=postgres;Port=5432;Database=koalabooks;Username=koalabooks;Password=__POSTGRES_PASSWORD__
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
      POSTGRES_PASSWORD: __POSTGRES_PASSWORD__
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

- [ ] **Step 2: Commit**

```bash
git add docker-compose.pr-preview.yml
git commit -m "feat: add per-PR docker compose template"
```

---

### Task 3: Update `docker-compose.yml` and `Caddyfile`

Add the `pr-previews` network and snippets directory mount to the Caddy service, and add the import directive to Caddyfile. When this reaches `main`, the existing `deploy.yml` applies these changes to the VM automatically.

**Files:**
- Modify: `docker-compose.yml`
- Modify: `Caddyfile`

- [ ] **Step 1: Update the `caddy` service in `docker-compose.yml`**

Replace the `caddy` service block with:

```yaml
  caddy:
    image: caddy:2-alpine
    ports:
      - "80:80"
      - "443:443"
    volumes:
      - ./Caddyfile:/etc/caddy/Caddyfile
      - ./caddy-snippets:/etc/caddy/snippets
      - caddy-data:/data
      - caddy-config:/config
    networks:
      - default
      - pr-previews
    depends_on:
      - web
    restart: unless-stopped
```

Then add a top-level `networks:` section at the end of `docker-compose.yml` (after the existing `volumes:` block):

```yaml
networks:
  pr-previews:
    external: true
```

- [ ] **Step 2: Add the import directive to `Caddyfile`**

```
import /etc/caddy/snippets/*.caddy

books.koalasoft.se {
    reverse_proxy web:8080
}

dashboard.koalasoft.se {
    reverse_proxy aspire-dashboard:18888
}
```

- [ ] **Step 3: Commit**

```bash
git add docker-compose.yml Caddyfile
git commit -m "feat: caddy snippets mount and pr-previews network"
```

---

### Task 4: Create `pr-preview.yml`

New workflow triggered on `pull_request` events. The `deploy` job builds and pushes the PR image, deploys to the VM, and posts a comment. The `cleanup` job tears everything down when the PR closes.

**Files:**
- Create: `.github/workflows/pr-preview.yml`

- [ ] **Step 1: Create `.github/workflows/pr-preview.yml`**

```yaml
name: PR Preview

on:
  pull_request:
    types: [opened, synchronize, reopened, closed]
    branches: [main]

env:
  REGISTRY: ghcr.io
  IMAGE_NAME: ${{ github.repository_owner }}/koalabooks-web

jobs:
  deploy:
    if: github.event.action != 'closed'
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
      pull-requests: write

    steps:
      - uses: actions/checkout@v6

      - name: Set up Docker Buildx
        uses: docker/setup-buildx-action@v4

      - name: Log in to GHCR
        uses: docker/login-action@v4
        with:
          registry: ${{ env.REGISTRY }}
          username: ${{ github.actor }}
          password: ${{ secrets.GITHUB_TOKEN }}

      - name: Build and push PR image
        uses: docker/build-push-action@v7
        with:
          context: .
          file: src/KoalaBooks.Web/Dockerfile
          platforms: linux/amd64,linux/arm64
          push: true
          tags: ${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:pr-${{ github.event.pull_request.number }}
          cache-from: type=registry,ref=${{ env.REGISTRY }}/${{ env.IMAGE_NAME }}:buildcache

      - name: Copy compose template to VM
        uses: appleboy/scp-action@v1.0.0
        with:
          host: ${{ secrets.DEPLOY_HOST }}
          username: ${{ secrets.DEPLOY_USER }}
          key: ${{ secrets.DEPLOY_SSH_KEY }}
          source: "docker-compose.pr-preview.yml"
          target: /opt/koalabooks

      - name: Deploy PR environment
        uses: appleboy/ssh-action@v1
        env:
          PR_NUMBER: ${{ github.event.pull_request.number }}
          OWNER: ${{ github.repository_owner }}
          PR_POSTGRES_PASSWORD: ${{ secrets.PR_POSTGRES_PASSWORD }}
          GHCR_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          GHCR_USER: ${{ github.actor }}
        with:
          host: ${{ secrets.DEPLOY_HOST }}
          username: ${{ secrets.DEPLOY_USER }}
          key: ${{ secrets.DEPLOY_SSH_KEY }}
          envs: PR_NUMBER,OWNER,PR_POSTGRES_PASSWORD,GHCR_TOKEN,GHCR_USER
          script: |
            echo "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin
            mkdir -p /opt/koalabooks/pr-${PR_NUMBER}
            sed \
              -e "s/__PR_NUMBER__/${PR_NUMBER}/g" \
              -e "s/__OWNER__/${OWNER}/g" \
              -e "s/__POSTGRES_PASSWORD__/${PR_POSTGRES_PASSWORD}/g" \
              /opt/koalabooks/docker-compose.pr-preview.yml \
              > /opt/koalabooks/pr-${PR_NUMBER}/docker-compose.yml
            printf 'pr-%s.books.koalasoft.se {\n    reverse_proxy pr-%s-web-1:8080\n}\n' \
              "${PR_NUMBER}" "${PR_NUMBER}" \
              > /opt/koalabooks/caddy-snippets/pr-${PR_NUMBER}.caddy
            cd /opt/koalabooks/pr-${PR_NUMBER}
            docker compose -p pr-${PR_NUMBER} pull
            docker compose -p pr-${PR_NUMBER} up -d
            docker exec koalabooks-caddy-1 caddy reload --config /etc/caddy/Caddyfile

      - name: Post or update PR comment
        uses: actions/github-script@v7
        with:
          script: |
            const prNumber = context.payload.pull_request.number;
            const url = `https://pr-${prNumber}.books.koalasoft.se`;
            const body = `<!-- pr-preview -->\n🚀 **Preview deployed:** [${url}](${url})`;

            const { data: comments } = await github.rest.issues.listComments({
              owner: context.repo.owner,
              repo: context.repo.repo,
              issue_number: prNumber,
            });
            const existing = comments.find(c => c.body.includes('<!-- pr-preview -->'));

            if (existing) {
              await github.rest.issues.updateComment({
                owner: context.repo.owner,
                repo: context.repo.repo,
                comment_id: existing.id,
                body,
              });
            } else {
              await github.rest.issues.createComment({
                owner: context.repo.owner,
                repo: context.repo.repo,
                issue_number: prNumber,
                body,
              });
            }

  cleanup:
    if: github.event.action == 'closed'
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write
      pull-requests: write

    steps:
      - name: Tear down PR environment
        uses: appleboy/ssh-action@v1
        env:
          PR_NUMBER: ${{ github.event.pull_request.number }}
          OWNER: ${{ github.repository_owner }}
          GHCR_TOKEN: ${{ secrets.GITHUB_TOKEN }}
          GHCR_USER: ${{ github.actor }}
        with:
          host: ${{ secrets.DEPLOY_HOST }}
          username: ${{ secrets.DEPLOY_USER }}
          key: ${{ secrets.DEPLOY_SSH_KEY }}
          envs: PR_NUMBER,OWNER,GHCR_TOKEN,GHCR_USER
          script: |
            echo "$GHCR_TOKEN" | docker login ghcr.io -u "$GHCR_USER" --password-stdin
            if [ -d /opt/koalabooks/pr-${PR_NUMBER} ]; then
              cd /opt/koalabooks/pr-${PR_NUMBER}
              docker compose -p pr-${PR_NUMBER} down -v || true
            fi
            docker rmi ghcr.io/${OWNER}/koalabooks-web:pr-${PR_NUMBER} || true
            rm -f /opt/koalabooks/caddy-snippets/pr-${PR_NUMBER}.caddy
            rm -rf /opt/koalabooks/pr-${PR_NUMBER}
            docker exec koalabooks-caddy-1 caddy reload --config /etc/caddy/Caddyfile

      - name: Delete GHCR package version
        uses: actions/github-script@v7
        with:
          script: |
            const prNumber = context.payload.pull_request.number;
            const tag = `pr-${prNumber}`;
            try {
              const { data: versions } = await github.rest.packages.getAllPackageVersionsForPackageOwnedByUser({
                package_type: 'container',
                package_name: 'koalabooks-web',
                username: context.repo.owner,
              });
              const version = versions.find(v => (v.metadata?.container?.tags ?? []).includes(tag));
              if (version) {
                await github.rest.packages.deletePackageVersionForUser({
                  package_type: 'container',
                  package_name: 'koalabooks-web',
                  username: context.repo.owner,
                  package_version_id: version.id,
                });
                console.log(`Deleted GHCR version for ${tag}`);
              } else {
                console.log(`No GHCR version found for ${tag}`);
              }
            } catch (e) {
              console.log(`Could not delete GHCR version: ${e.message}`);
            }

      - name: Delete PR preview comment
        uses: actions/github-script@v7
        with:
          script: |
            const prNumber = context.payload.pull_request.number;
            const { data: comments } = await github.rest.issues.listComments({
              owner: context.repo.owner,
              repo: context.repo.repo,
              issue_number: prNumber,
            });
            const preview = comments.find(c => c.body.includes('<!-- pr-preview -->'));
            if (preview) {
              await github.rest.issues.deleteComment({
                owner: context.repo.owner,
                repo: context.repo.repo,
                comment_id: preview.id,
              });
            }
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/pr-preview.yml
git commit -m "feat: pr preview deploy and cleanup workflow"
```

---

### Task 5: Create `pr-preview-cleanup.yml` (weekly cron)

Runs every Sunday at 02:00 UTC. Deletes any `pr-*` images in GHCR and any VM directories/snippets whose PR is no longer open — catches anything that leaked through if the cleanup job failed.

**Files:**
- Create: `.github/workflows/pr-preview-cleanup.yml`

- [ ] **Step 1: Create `.github/workflows/pr-preview-cleanup.yml`**

```yaml
name: PR Preview Cleanup

on:
  schedule:
    - cron: '0 2 * * 0'

jobs:
  cleanup:
    runs-on: ubuntu-latest
    permissions:
      contents: read
      packages: write

    steps:
      - name: Get open PR numbers
        id: open-prs
        uses: actions/github-script@v7
        with:
          result-encoding: string
          script: |
            const { data: prs } = await github.rest.pulls.list({
              owner: context.repo.owner,
              repo: context.repo.repo,
              state: 'open',
              per_page: 100,
            });
            return prs.map(pr => String(pr.number)).join(',');

      - name: Delete stale GHCR images
        uses: actions/github-script@v7
        with:
          script: |
            const openSet = new Set('${{ steps.open-prs.outputs.result }}'.split(',').filter(Boolean));
            try {
              const { data: versions } = await github.rest.packages.getAllPackageVersionsForPackageOwnedByUser({
                package_type: 'container',
                package_name: 'koalabooks-web',
                username: context.repo.owner,
                per_page: 100,
              });
              for (const v of versions) {
                const prTag = (v.metadata?.container?.tags ?? []).find(t => /^pr-\d+$/.test(t));
                if (!prTag) continue;
                const prNum = prTag.replace('pr-', '');
                if (!openSet.has(prNum)) {
                  console.log(`Deleting stale GHCR image ${prTag}`);
                  await github.rest.packages.deletePackageVersionForUser({
                    package_type: 'container',
                    package_name: 'koalabooks-web',
                    username: context.repo.owner,
                    package_version_id: v.id,
                  });
                }
              }
            } catch (e) {
              console.log(`GHCR cleanup error: ${e.message}`);
            }

      - name: Prune orphaned VM directories and Caddy snippets
        uses: appleboy/ssh-action@v1
        env:
          OPEN_PR_NUMBERS: ${{ steps.open-prs.outputs.result }}
        with:
          host: ${{ secrets.DEPLOY_HOST }}
          username: ${{ secrets.DEPLOY_USER }}
          key: ${{ secrets.DEPLOY_SSH_KEY }}
          envs: OPEN_PR_NUMBERS
          script: |
            IFS=',' read -ra OPEN_PRS <<< "$OPEN_PR_NUMBERS"
            RELOADED=0
            for snippet in /opt/koalabooks/caddy-snippets/pr-*.caddy; do
              [ -f "$snippet" ] || continue
              pr_num=$(basename "$snippet" .caddy | sed 's/pr-//')
              is_open=0
              for open in "${OPEN_PRS[@]}"; do
                [ "$open" = "$pr_num" ] && is_open=1 && break
              done
              if [ "$is_open" = "0" ]; then
                echo "Pruning stale PR ${pr_num}"
                if [ -d /opt/koalabooks/pr-${pr_num} ]; then
                  cd /opt/koalabooks/pr-${pr_num}
                  docker compose -p pr-${pr_num} down -v || true
                fi
                rm -f /opt/koalabooks/caddy-snippets/pr-${pr_num}.caddy
                rm -rf /opt/koalabooks/pr-${pr_num}
                RELOADED=1
              fi
            done
            [ "$RELOADED" = "1" ] && docker exec koalabooks-caddy-1 caddy reload --config /etc/caddy/Caddyfile || true
```

- [ ] **Step 2: Commit**

```bash
git add .github/workflows/pr-preview-cleanup.yml
git commit -m "feat: weekly cron to prune orphaned pr preview artifacts"
```

---

### Task 6: Verify end-to-end

Manual verification — no automated tests possible for GitHub Actions workflows.

- [ ] **Step 1: Push this branch and open a PR against `main`**

Watch the `PR Preview` workflow run in the Actions tab. The `deploy` job should complete in ~5 minutes (multi-platform build takes most of the time).

Expected: workflow succeeds, a comment appears on the PR:
> 🚀 **Preview deployed:** https://pr-{n}.books.koalasoft.se

- [ ] **Step 2: Visit the preview URL**

Expected: KoalaBooks loads. Login works. Data is completely separate from production.

- [ ] **Step 3: Push another commit to the branch**

Expected: `deploy` job runs again. The existing PR comment is updated (not a new comment added).

- [ ] **Step 4: Close the PR**

Expected: `cleanup` job runs. The PR comment disappears. Visiting the preview URL returns a 404/502. 

- [ ] **Step 5: Verify VM state after cleanup**

SSH into the VM:

```bash
ls /opt/koalabooks/caddy-snippets/
# expected: empty (or contains only snippets for other open PRs)

ls /opt/koalabooks/ | grep pr-
# expected: no pr-{n}/ directory

docker ps --filter name=pr-
# expected: no containers

docker images ghcr.io/*/koalabooks-web --format '{{.Tag}}' | grep ^pr-
# expected: no pr-{n} tags
```
