#!/usr/bin/env bash
# dev-db.sh — diagnose and recover the local MS SQL `db` dependency.
#
# Why this exists (MAS-727 / MAS-726):
#   `docker compose up` reported the terse, misleading error
#       "dependency failed to start: container trustlist-db is unhealthy"
#   The real cause is almost always a STALE DB VOLUME:
#   MS SQL Server only applies MSSQL_SA_PASSWORD the *first* time the
#   `mssql-data` volume is initialised. If you later regenerate
#   MSSQL_SA_PASSWORD in `.env` (the README quick-start does exactly this
#   with `openssl rand`) but the old volume still exists, the server keeps
#   the OLD password. The healthcheck then logs in with the NEW password,
#   gets "Login failed for user 'sa'", never goes healthy, and the `api`
#   service refuses to start with the generic "dependency db failed" message.
#
# This script turns that silent footgun into a loud, actionable diagnosis
# and gives you a one-command recovery.
#
# Usage:
#   scripts/dev-db.sh check     # diagnose: does the running db accept the .env sa password?
#   scripts/dev-db.sh reset     # recreate the db volume so it re-inits with the current .env password
#   scripts/dev-db.sh up        # guarded `compose up`: check first, fail loud with guidance
#
set -euo pipefail

REPO_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$REPO_ROOT"

DB_CONTAINER="trustlist-db"
# Default compose volume name = "<project>_<volume>"; project defaults to dir name.
PROJECT="${COMPOSE_PROJECT_NAME:-$(basename "$REPO_ROOT")}"
VOLUME="${PROJECT}_mssql-data"

red()   { printf '\033[31m%s\033[0m\n' "$*"; }
green() { printf '\033[32m%s\033[0m\n' "$*"; }
yellow(){ printf '\033[33m%s\033[0m\n' "$*"; }

load_env() {
  if [[ ! -f .env ]]; then
    red "ERROR: .env not found. Copy .env.example to .env first (see README 'Run it')."
    exit 2
  fi
  # shellcheck disable=SC1091
  set -a; . ./.env; set +a
  if [[ -z "${MSSQL_SA_PASSWORD:-}" || "${MSSQL_SA_PASSWORD}" == CHANGE_ME* ]]; then
    red "ERROR: MSSQL_SA_PASSWORD is unset or still the .env.example placeholder."
    exit 2
  fi
}

# Returns 0 if the running db accepts the current .env sa password.
probe_login() {
  docker exec "$DB_CONTAINER" /opt/mssql-tools18/bin/sqlcmd \
    -S localhost -U sa -P "${MSSQL_SA_PASSWORD}" -C -Q 'SELECT 1' >/dev/null 2>&1
}

container_running() {
  [[ "$(docker inspect -f '{{.State.Running}}' "$DB_CONTAINER" 2>/dev/null)" == "true" ]]
}

cmd_check() {
  load_env
  if ! container_running; then
    yellow "db container '$DB_CONTAINER' is not running. Start it with: scripts/dev-db.sh up"
    return 0
  fi
  if probe_login; then
    green "OK: '$DB_CONTAINER' accepts the MSSQL_SA_PASSWORD from .env. The db dependency is healthy."
    return 0
  fi

  red "DIAGNOSIS: the running db rejects the MSSQL_SA_PASSWORD in your .env."
  echo
  echo "  This is the MAS-726 'dependency db failed to start' root cause:"
  echo "  the '$VOLUME' volume was initialised with a DIFFERENT sa password"
  echo "  than the one now in .env. MS SQL only applies MSSQL_SA_PASSWORD on the"
  echo "  first init of the data volume, so the old password persists."
  echo
  echo "  Recent server-side login errors:"
  docker logs "$DB_CONTAINER" 2>&1 | grep -i "Login failed for user 'sa'" | tail -3 | sed 's/^/    /' || true
  echo
  yellow "  FIX (destroys local dev data only): scripts/dev-db.sh reset"
  return 1
}

cmd_reset() {
  load_env
  yellow "This will STOP the stack and DELETE the local db volume '$VOLUME'."
  yellow "All local dev data in the Trustlist database will be lost (migrations re-run on next up)."
  docker compose down 2>/dev/null || true
  if docker volume inspect "$VOLUME" >/dev/null 2>&1; then
    docker volume rm "$VOLUME"
    green "Removed volume '$VOLUME'."
  else
    yellow "Volume '$VOLUME' did not exist (nothing to remove)."
  fi
  green "Done. Bring the stack back up with: docker compose up -d --build"
}

cmd_up() {
  load_env
  docker compose up -d --build db
  echo "Waiting for db to accept the .env sa password (up to ~120s)..."
  for _ in $(seq 1 24); do
    if container_running && probe_login; then
      green "db is healthy. Starting api + web..."
      docker compose up -d --build
      return 0
    fi
    sleep 5
  done
  echo
  red "db did not become healthy with the current .env password."
  cmd_check || true
  exit 1
}

case "${1:-check}" in
  check) cmd_check ;;
  reset) cmd_reset ;;
  up)    cmd_up ;;
  *) echo "Usage: $0 {check|reset|up}" >&2; exit 64 ;;
esac
