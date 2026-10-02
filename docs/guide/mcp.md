# MCP setup

GroundKit supports both standard MCP transports:

- `stdio` is the default and is best for one agent process per session.
- Streamable HTTP is available at `/mcp` for multiple clients or a container.

Start the local server:

During development, use the root launcher so no additional `PATH` setup is needed:

```bash
groundkit mcp
```

Start the Streamable HTTP server locally:

```bash
groundkit mcp --http
groundkit mcp --http 4000
groundkit mcp --http 4000 --host 0.0.0.0
```

The default HTTP endpoint is `http://127.0.0.1:4000/mcp`.
Use `--libs react,vite` with either transport to restrict the session.

For Docker, start the MCP service from the repository root:

```bash
docker compose up --build mcp
```

The container exposes `http://localhost:8081/mcp` and stores its package data
in the `mcp-data` volume. Install or import packages into the mounted
`GROUNDKIT_HOME` before querying them.

The Docker image defaults to Streamable HTTP, but stdio is also supported:

```bash
docker compose run --rm -i mcp-stdio
```

Use `mcp-stdio` when an MCP client launches Docker as its subprocess. The
container communicates over stdin/stdout and does not publish a network port.
Both Docker services use the same `mcp-data` package volume.

```powershell
groundkit mcp
```

```bash
groundkit mcp
```

For an installed or published executable, configure an MCP client with:

```json
{
  "mcpServers": {
    "groundkit": {
      "command": "groundkit-mcp"
    }
  }
}
```

For an HTTP-capable MCP host, configure the URL directly:

```json
{
  "mcpServers": {
    "groundkit": {
      "type": "streamable-http",
      "url": "http://localhost:8081/mcp"
    }
  }
}
```

To restrict the server to specific installed packages, pass `--libs` or `-l`:

```json
{
  "mcpServers": {
    "groundkit": {
      "command": "groundkit-mcp",
      "args": ["-l", "react,vite"]
    }
  }
}
```

## Recommended agent flow

1. Call `resolve-source` with the library name. Use `library_catalog` when choosing from starter sources.
2. Confirm returned package ID and version match the project dependency.
3. If the package is missing, call `search_packages` with registry, package name, and optional exact version.
4. Download the selected version with `download_package`. This is the only flow step that needs registry access.
5. Call `query-docs` with the installed package ID and a short API name, symbol, error code, or topic.
6. Answer from returned sections. If no useful hits return, resolve the source or refine the query; do not silently fall back to invented documentation.

`get_docs` is a compatibility alias for `query-docs`. Both return the same structured payload: package ID, version, token total, and focused hits with document and section titles, content, code presence, and relevance scores.

The equivalent CLI command is:

```bash
groundkit query nextjs "middleware authentication"
groundkit query "nextjs@16.0" "middleware authentication"
```

A bare name selects the newest installed version. `name@version` selects that
exact installed version. CLI output uses the same JSON fields as `get_docs`.

## Query shape

```json
{
  "packageId": "react",
  "topic": "useEffect cleanup",
  "maxTokens": 2000,
  "maxHits": 8,
  "relativeScoreCutoff": 0.5
}
```

Use a lower token or hit limit for a narrow lookup. Raise it only when the topic needs surrounding context. The score ranks matches; it is not a confidence or truth probability.

## Trust boundaries

The server queries installed packages locally and does not fetch documentation during `query-docs`. Package provenance and freshness come from the package manifest and source metadata. Registry results and downloads are external inputs, so pin versions and inspect source metadata when reproducibility matters.

MCP makes retrieval available to an agent; it does not make the final answer automatically correct. The host agent should distinguish missing evidence from a negative fact, preserve conflicting source information, and verify high-impact changes.
