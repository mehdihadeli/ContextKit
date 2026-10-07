#!/usr/bin/env bash
set -euo pipefail

cleanup() {
  docker compose down --volumes --remove-orphans
}
trap cleanup EXIT

docker compose build mcp mcp-stdio
docker compose up --detach mcp

initialize_request='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-03-26","capabilities":{},"clientInfo":{"name":"GroundKit.DockerTests","version":"1.0"}}}'

curl --fail --silent --show-error --retry 20 --retry-all-errors --retry-connrefused \
  --header 'Accept: application/json, text/event-stream' \
  --header 'Content-Type: application/json' \
  --data "$initialize_request" \
  http://localhost:8081/mcp | grep -q '"result"'

{ printf '%s\n' "$initialize_request"; sleep 1; } \
  | timeout 30s docker compose run --rm --no-TTY mcp-stdio \
  | grep -q '"result"'