#!/bin/bash
set -euo pipefail
BACKUP_DIR="/opt/koalabooks/backups"
mkdir -p "$BACKUP_DIR"
TIMESTAMP=$(date +%Y%m%d_%H%M%S)
docker compose -f /opt/koalabooks/docker-compose.yml exec -T postgres \
  pg_dump -U koalabooks koalabooks | gzip > "$BACKUP_DIR/koalabooks_$TIMESTAMP.sql.gz"
# Keep only the last 30 days
find "$BACKUP_DIR" -name "*.sql.gz" -mtime +30 -delete
