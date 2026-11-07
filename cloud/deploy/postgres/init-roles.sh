#!/bin/sh
# Runs once ONLY on initialization of a fresh PostgreSQL data volume.
# Does not alter or migrate an existing volume. Do NOT run on production manually.
set -eu
umask 077

app_password="$(cat /run/secrets/db_app_password)"
migration_password="$(cat /run/secrets/db_migrator_password)"
[ "${#app_password}" -ge 24 ] || { echo >&2 "App DB password must be 24+ chars"; exit 1; }
[ "${#migration_password}" -ge 24 ] || { echo >&2 "Migration DB password must be 24+ chars"; exit 1; }

# SQL literals are escaped without echoing or exposing passwords on the
# process command line. This bootstrap runs only inside a fresh DB container.
app_quoted="$(printf '%s' "$app_password" | sed "s/'/''/g")"
migration_quoted="$(printf '%s' "$migration_password" | sed "s/'/''/g")"

psql -v ON_ERROR_STOP=1 --username "${POSTGRES_USER}" --dbname "${POSTGRES_DB}" <<SQL
CREATE ROLE aerohelm_migrator LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '${migration_quoted}';
CREATE ROLE aerohelm_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION PASSWORD '${app_quoted}';
ALTER DATABASE aerohelm OWNER TO aerohelm_migrator;
REVOKE ALL ON DATABASE aerohelm FROM PUBLIC;
GRANT CONNECT ON DATABASE aerohelm TO aerohelm_app;
GRANT USAGE, CREATE ON SCHEMA public TO aerohelm_migrator;
GRANT USAGE ON SCHEMA public TO aerohelm_app;
ALTER DEFAULT PRIVILEGES FOR ROLE aerohelm_migrator IN SCHEMA public
  GRANT SELECT, INSERT, UPDATE, DELETE ON TABLES TO aerohelm_app;
ALTER DEFAULT PRIVILEGES FOR ROLE aerohelm_migrator IN SCHEMA public
  GRANT USAGE, SELECT ON SEQUENCES TO aerohelm_app;
SQL
unset app_password migration_password app_quoted migration_quoted
