#!/usr/bin/env bash

set -euo pipefail

script_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
repository="$(cd "$script_dir/.." && pwd)"
runtime=""
output="artifacts"
version=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --runtime|--output|--version)
      if [[ $# -lt 2 || -z "$2" || "$2" == --* ]]; then
        echo "Missing value for $1." >&2
        exit 2
      fi
      case "$1" in
        --runtime) runtime="$2" ;;
        --output) output="$2" ;;
        --version) version="$2" ;;
      esac
      shift 2
      ;;
    *)
      echo "Usage: bash scripts/package-semantic-provider.sh --runtime <RID> [--output <directory>] [--version <version>]" >&2
      exit 2
      ;;
  esac
done

case "$runtime" in
  win-x64|linux-x64|osx-x64|osx-arm64) ;;
  *) echo "Unsupported runtime '$runtime'. Supported: win-x64, linux-x64, osx-x64, osx-arm64." >&2; exit 2 ;;
esac

for tool in dotnet jq zip sha256sum find sort wc; do
  command -v "$tool" >/dev/null || { echo "Required tool missing: $tool." >&2; exit 1; }
done

mkdir -p "$output"
output="$(cd "$output" && pwd)"
bundle="$output/semantic-provider/$runtime"
archive="$output/groundkit-semantic-onnx-$runtime.zip"
manifest="$output/groundkit-semantic-onnx-$runtime.json"
mkdir -p "$bundle"
if [[ -n "$(find "$bundle" -mindepth 1 -print -quit)" ]]; then
  echo "Bundle directory must be empty: $bundle." >&2
  exit 1
fi
if [[ -e "$archive" || -e "$manifest" ]]; then
  echo "Provider release assets already exist: $archive or $manifest." >&2
  exit 1
fi

dotnet publish "$repository/src/ContextKit.Semantic.Onnx/ContextKit.Semantic.Onnx.csproj" \
  --configuration Release --runtime "$runtime" --self-contained false \
  --output "$bundle" -p:UseAppHost=false -p:DebugType=None -p:DebugSymbols=false

if [[ -z "$version" ]]; then
  version="$(jq -er '.libraries | keys[] | select(startswith("GroundKit.Semantic.Onnx/")) | split("/")[1] | split("+")[0]' \
    "$bundle/ContextKit.Semantic.Onnx.deps.json")"
fi

files='{}'
while IFS= read -r -d '' file; do
  relative="${file#"$bundle/"}"
  hash="$(sha256sum "$file")"
  files="$(jq --arg path "$relative" --arg hash "${hash%% *}" '. + {($path): $hash}' <<< "$files")"
done < <(find "$bundle" -type f -print0 | LC_ALL=C sort -z)

(cd "$bundle" && zip -q -r "$archive" .)
archive_hash="$(sha256sum "$archive")"
size="$(wc -c < "$archive")"
jq -n --arg version "$version" --arg runtime "$runtime" \
  --arg hash "${archive_hash%% *}" --argjson size "$size" --argjson files "$files" \
  '{ProtocolVersion: 1, Version: $version, RuntimeIdentifier: $runtime, Sha256: $hash, Size: $size, Files: $files}' \
  > "$manifest"
cp "$manifest" "$bundle/provider.json"

echo "Provider bundle: $bundle"
echo "Provider archive: $archive"