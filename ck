#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
project="$script_dir/src/ContextKit.Cli/ContextKit.Cli.csproj"

dotnet run --project "$project" --no-restore -v:q -- "$@"