#!/usr/bin/env sh
set -eu

backup_dir=${1:-}
suffix=${2:-}
if [ -z "$backup_dir" ] || [ -z "$suffix" ]; then
  echo "Usage: $0 BACKUP_DIRECTORY _restore_SUFFIX" >&2
  exit 2
fi
case "$suffix" in
  _restore_*) ;;
  *)
    echo "Restore suffix must match _restore_[a-z0-9_]*" >&2
    exit 2
    ;;
esac
suffix_value=${suffix#_restore_}
case "$suffix_value" in
  ''|*[!a-z0-9_]*)
    echo "Restore suffix must match _restore_[a-z0-9_]*" >&2
    exit 2
    ;;
esac
if [ "${#suffix}" -gt 32 ]; then
  echo "Restore suffix must contain at most 32 characters" >&2
  exit 2
fi

test -f "$backup_dir/DATABASES.tsv"
test -f "$backup_dir/SHA256SUMS"
(cd "$backup_dir" && sha256sum --check SHA256SUMS)

while read -r service database; do
  case "$service:$database" in
    postgres-identity:identity|postgres-family:familygraph|postgres-room:room|postgres-message:message|postgres-notification:notification) ;;
    *)
      echo "Unexpected database mapping: $service $database" >&2
      exit 2
      ;;
  esac

  target="${database}${suffix}"
  dump="$backup_dir/$database.dump"
  test -s "$dump"
  echo "Restoring $database into isolated database $target"
  docker compose exec -T "$service" dropdb \
    --username app --if-exists --force "$target"
  docker compose exec -T "$service" createdb --username app "$target"
  docker compose exec -T "$service" pg_restore \
    --username app --dbname "$target" --no-owner --no-privileges < "$dump"

  table_count=$(docker compose exec -T "$service" psql \
    --username app --dbname "$target" --tuples-only --no-align \
    --command "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public'" | tr -d '\r')
  if [ "$table_count" -lt 1 ]; then
    echo "Restore verification found no public tables in $target" >&2
    exit 1
  fi
  echo "Verified $target with $table_count public tables"

  if [ "${CLEANUP_RESTORED_DATABASES:-0}" = "1" ]; then
    docker compose exec -T "$service" dropdb \
      --username app --if-exists --force "$target"
  fi
done < "$backup_dir/DATABASES.tsv"

echo "Restore drill complete"
