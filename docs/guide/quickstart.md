# Quickstart

GroundKit turns documentation into local packages and serves those packages to MCP-compatible AI agents.

## Prerequisites

- .NET SDK 10
- Git, when building from a repository
- An MCP-compatible client such as VS Code, Claude Desktop, or Cursor

Before using the examples below during development, add the repository root to
`PATH` for the current shell session so `groundkit` resolves to the repo-local
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
groundkit add https://github.com/vuejs/docs --docs-path src
```

Local repositories can use automatic docs-folder detection or an explicit path and package metadata:

```bash
groundkit add ./my-lib --path docs --name my-library --pkg-version 1.0.0
```

For Git sources, pin a tag for reproducible packages:

```bash
groundkit add https://github.com/mattpocock/skills --tag main
```

Build one of the curated sources by name:

```bash
groundkit catalog
groundkit add react
```

## Install a published package

Skip the build when the package already exists in the registry. `install`
resolves the catalog entry, pulls the OCI layer from GHCR, verifies size and
SHA-256, and imports it into the local store:

```bash
groundkit search-packages npm react
groundkit install npm/react
groundkit install npm/angular 20.3.15
```

If the registry has no match or cannot be reached, the same command falls back
to cloning the catalog repository and building locally. `download-package` is the
strict registry-only alternative with no fallback, and `add` always builds from
source.

## End-to-end example: install from the registry, then query

This uses the public catalog and needs no credentials. The outputs below are from
an actual run.

**1. Find the version.** `search-packages` reads catalog metadata only.

```bash
groundkit search-packages npm axios
```

```text
┌─────────┬─────────┬───────────────────────────┬───────────┐
│ Package │ Version │ Description               │ Size      │
├─────────┼─────────┼───────────────────────────┼───────────┤
│ axios   │ latest  │ Promise-based HTTP client │ 356,352 B │
└─────────┴─────────┴───────────────────────────┴───────────┘
```

**2. Install it.** The catalog lists `npm/axios`, so GroundKit resolves the GHCR
manifest digest, downloads the artifact, verifies its byte size and SHA-256
against the catalog, and imports it into the local store. The command reports the
path it wrote.

```bash
groundkit install npm/axios
```

**3. Confirm the package.** This reads the local store and makes no request.

```bash
groundkit list
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
groundkit query axios "request interceptors"
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
| `query` | none | local SQLite |

Only discovery and download use the network. Once installed, the package works
offline and an agent session can query it repeatedly without a hosted dependency.

For a library published in several versions, pin the one your project uses:

```bash
groundkit install npm/angular 20.3.15
groundkit query 'angular@20.3.15' 'component lifecycle'
```

`angular` publishes `21.0.3`, `20.3.15`, and `19.2.17`. Use `library@version`
once more than one version is installed; a bare name selects the newest
installed version.

## Query it

```bash
groundkit list
groundkit query react "useEffect cleanup"
```

`list` shows installed package name, version, SQLite package size, document count, section count, and a totals summary.

Packages live under `.groundkit/packages` by default. Set `GROUNDKIT_HOME` to move that store.

Inspect package provenance before querying it:

```bash
groundkit inspect my-library
```

## Start MCP

```bash
dotnet run --project src/GroundKit.Mcp
```

The server uses stdio transport. Add it to your agent configuration using the command and arguments shown in [MCP setup](/guide/mcp).
