#!/usr/bin/env sh
set -eu

output_dir=${1:-}
if [ -z "$output_dir" ]; then
  echo "Usage: $0 OUTPUT_DIRECTORY" >&2
  exit 2
fi

mkdir -p "$output_dir"
if find "$output_dir" -mindepth 1 -maxdepth 1 -print -quit | grep -q .; then
  echo "Backup directory must be empty: $output_dir" >&2
  exit 2
fi

databases='postgres-identity identity
postgres-family familygraph
postgres-room room
postgres-message message
postgres-notification notification'

echo "$databases" > "$output_dir/DATABASES.tsv"
echo "$databases" | while read -r service database; do
  target="$output_dir/$database.dump"
  temporary="$target.partial"
  echo "Backing up $database from $service"
  docker compose exec -T "$service" \
    pg_dump --username app --dbname "$database" --format custom \
      --no-owner --no-privileges > "$temporary"
  test -s "$temporary"
  mv "$temporary" "$target"
done

(
  cd "$output_dir"
  sha256sum DATABASES.tsv ./*.dump > SHA256SUMS
)
echo "Backup complete: $output_dir"
