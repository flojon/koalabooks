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
