# Quickstart

ContextKit turns documentation into local packages and serves those packages to MCP-compatible AI agents.

## Prerequisites

- .NET SDK 10
- Git, when building from a repository
- An MCP-compatible client such as VS Code, Claude Desktop, or Cursor

## Install the CLI

Install the published tool:

```bash
dotnet tool install --global ContextKit   # installs the `ck` command
```

The installed tool responds to `ck`, and the repository launchers respond to
both `ck` and `groundkit`, so either name works in every example below.

Before using the examples below during development, add the repository root to
`PATH` for the current shell session so `ck` resolves to the repo-local
wrapper:

```bash
export PATH="$PWD:$PATH"
```

```powershell
$env:Path = "$PWD;$env:Path"
```

## Build a package

Build documentation from a repository:

```bash
ck add https://github.com/vuejs/docs --docs-path src
```

Local repositories can use automatic docs-folder detection or an explicit path and package metadata:

```bash
ck add ./my-lib --path docs --name my-library --pkg-version 1.0.0
```

For Git sources, pin a tag for reproducible packages:

```bash
ck add https://github.com/mattpocock/skills --tag main
```

Build one of the curated sources by name:

```bash
ck catalog
ck add react
```

## Install a published package

Skip the build when the package already exists in the registry. `install`
resolves the catalog entry, pulls the OCI layer from GHCR, verifies size and
SHA-256, and imports it into the local store:

```bash
ck search-packages npm react
ck install npm/react
ck install npm/angular 20.3.15
```

If the registry has no match or cannot be reached, the same command falls back
to cloning the catalog repository and building locally. `download-package` is the
strict registry-only alternative with no fallback, and `add` always builds from
source.

## Choose a search mode

The standard tool uses lexical search (SQLite FTS5/BM25), with no ONNX dependency
or model download. After installing a package, query API names and keywords:

```bash
ck query react "useEffect cleanup" --search-mode lexical
```

For paraphrases, optionally install the separate provider and model:

```bash
ck semantic provider install onnx
ck semantic model install bge-micro-v2
ck query react "stop work when a component disappears" --search-mode semantic
ck query react "useEffect cleanup for background work" --search-mode hybrid
```

Semantic uses local embeddings; hybrid combines embedding and BM25 rankings.
Installing assets leaves lexical as the default. Run `ck semantic enable --mode hybrid`
to change the default, or keep using per-query overrides. `ck semantic disable`
restores lexical without deleting assets. See the
[Search mode guide](/guide/grounding#choosing-a-search-mode) for indexing, MCP
arguments, model limits, and troubleshooting.

## End-to-end example: install from the registry, then query

This uses the public catalog and needs no credentials. The outputs below are from
an actual run.

**1. Find the version.** `search-packages` reads catalog metadata only.

```bash
ck search-packages npm axios
```

```text
┌─────────┬─────────┬───────────────────────────┬───────────┐
│ Package │ Version │ Description               │ Size      │
├─────────┼─────────┼───────────────────────────┼───────────┤
│ axios   │ latest  │ Promise-based HTTP client │ 356,352 B │
└─────────┴─────────┴───────────────────────────┴───────────┘
```

**2. Install it.** The catalog lists `npm/axios`, so ContextKit resolves the GHCR
manifest digest, downloads the artifact, verifies its byte size and SHA-256
against the catalog, and imports it into the local store. The command reports the
path it wrote.

```bash
ck install npm/axios
```

**3. Confirm the package.** This reads the local store and makes no request.

```bash
ck list
```

```text
                               Installed packages
┌──────────────┬──────────────┬───────────────┬─────────────────┬──────────────┐
│ Package      │ Version      │          Size │       Documents │     Sections │
├──────────────┼──────────────┼───────────────┼─────────────────┼──────────────┤
│ axios        │ latest       │      348.0 KB │              16 │           80 │
└──────────────┴──────────────┴───────────────┴─────────────────┴──────────────┘
```

**4. Query it.** Retrieval is fully local.

```bash
ck query axios "request interceptors"
```

```json
{
  "packageId": "axios",
  "version": "latest",
  "totalTokens": 1342,
  "hits": [
    {
      "documentTitle": "interceptors",
      "sectionTitle": "interceptors",
      "tokenEstimate": 588,
      "hasCode": true,
      "score": 6.6259339612707535
    }
  ]
}
```

| Step | Network | Reads |
| --- | --- | --- |
| `search-packages` | catalog `index.json` | registry metadata |
| `install` | catalog + `ghcr.io` | package bytes |
| `list` | none | local store |
| `query` | none when the package is installed | local SQLite |

Only discovery and download use the network. Once installed, the package works
offline and an agent session can query it repeatedly without a hosted dependency.

You do not have to install first. `query` downloads a published package on demand
and then searches it, so this single command is enough:

```bash
# Downloads npm/vue@latest from the registry, then queries it
ck query vue "composition api"
```

Add `--no-install` to keep the command offline. When the package is neither
installed nor published, `query` exits with `1` and prints the two routes that
would fix it: install the published artifact, or build the package once from a
repository or a local docs folder with `ck add`, then query it offline.

For a library published in several versions, you do not need the exact one:

```bash
# First run: `19` resolves to the newest 19.x.y, here 19.2.17, and is downloaded
ck query 'angular@19' 'component lifecycle'
# Resolved angular@19 to npm/angular@19.2.17.

# Second run: the same selector is answered from the local store, with no network access
ck query 'angular@19' 'component lifecycle'
# Using installed angular@19.2.17 for angular@19.
```

A partial version is resolved against the local store first, then against the
registry. A version that matches nothing resolves to nothing, so the command
reports it instead of quietly answering from a different release line:

```bash
ck query 'angular@18' 'component lifecycle'
# Version 18 of npm/angular is not published. Published versions: 21.0.3, 20.3.15, 19.2.17.
```

Pin the exact version when it matters:

```bash
ck install npm/angular 20.3.15
ck query 'angular@20.3.15' 'component lifecycle'
```

`angular` publishes `21.0.3`, `20.3.15`, and `19.2.17`. Use `library@version`
once more than one version is installed; a bare name selects the newest
installed version.

## Query it

```bash
ck list
ck query react "useEffect cleanup"
```

`list` shows installed package name, version, SQLite package size, document count, section count, and a totals summary.

Packages live under `.groundkit/packages` by default. Set `GROUNDKIT_HOME` to move that store.

Inspect package provenance before querying it:

```bash
ck inspect my-library
```

## Start MCP

```bash
ck mcp          # stdio, the default transport
ck mcp --http   # Streamable HTTP at http://127.0.0.1:4000/mcp
```

The server reads packages from the local store and never fetches the internet
during a query. Add it to your agent configuration with the command and
arguments shown in [MCP setup](/guide/mcp).
