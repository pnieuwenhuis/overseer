#!/usr/bin/env bash
# Runs an Overseer image with its default command against a throwaway PostgreSQL and checks
# that the Portal comes up showing the version the image is tagged with (on its login page).
#
#   scripts/smoke-test-image.sh <image>:<version>
set -euo pipefail

image="${1:?usage: $0 <image>:<version>}"
version="${image##*:}"
name="overseer-smoke-$$"

cleanup() {
  docker rm -f "$name-app" "$name-db" >/dev/null 2>&1 || true
  docker network rm "$name" >/dev/null 2>&1 || true
}
trap cleanup EXIT

docker network create "$name" >/dev/null
docker run -d --name "$name-db" --network "$name" \
  -e POSTGRES_DB=overseer -e POSTGRES_USER=overseer -e POSTGRES_PASSWORD=overseer \
  postgres:17-alpine >/dev/null
until docker exec "$name-db" pg_isready -U overseer -d overseer -h localhost >/dev/null 2>&1; do sleep 1; done

# serve refuses to start without a password hash. This one is of a throwaway password the test never uses.
password_hash='AQAAAAIAAYagAAAAEIv7raqwKxDJdULThyduHuiA085v6w7W0K5OvFHk1q4sg7fzszrCi1VyDuWsDjkynA=='

docker run -d --name "$name-app" --network "$name" -p 127.0.0.1::8080 \
  -e "OVERSEER_CONNECTIONSTRINGS__OVERSEER=Host=$name-db;Database=overseer;Username=overseer;Password=overseer" \
  -e "OVERSEER_USER__PASSWORDHASH=$password_hash" \
  "$image" >/dev/null
port="$(docker port "$name-app" 8080/tcp | head -n1 | sed 's/.*://')"

for _ in $(seq 1 60); do
  if page="$(curl -fsSL "http://127.0.0.1:$port/" 2>/dev/null)"; then
    if grep -qF "Overseer $version<" <<<"$page"; then
      echo "Portal is up and shows Overseer $version."
      exit 0
    fi
    echo "Portal is up but does not show Overseer $version." >&2
    exit 1
  fi
  sleep 1
done

echo "Portal did not come up. Container log:" >&2
docker logs "$name-app" >&2
exit 1
