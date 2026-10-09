# Integrations

## Connect any MCP-compatible host

ContextKit supports standard stdio and Streamable HTTP transports. Use stdio when an MCP host owns one server process; use HTTP when multiple clients share a server at `/mcp`.

## VS Code and Copilot

Add a workspace MCP configuration in `.vscode/mcp.json`:

```json
{
  "servers": {
    "groundkit": {
      "type": "stdio",
      "command": "ck",
      "args": ["mcp"]
    }
  }
}
```

Click **Start** above the server, then use Agent mode in Copilot Chat. The MCP
server also ships as its own tool shim, `groundkit-mcp`, when a single-purpose
executable is easier to point at.

## Claude Desktop, Cursor, and other hosts

Most MCP clients accept the same JSON server entry; only the file location
differs:

| Client | Configuration file |
| --- | --- |
| VS Code (GitHub Copilot) | `.vscode/mcp.json` in the workspace |
| Claude Code | `claude mcp add ck -- ck mcp` |
| Claude Desktop | `%APPDATA%\Claude\claude_desktop_config.json` (Windows), `~/Library/Application Support/Claude/` (macOS), `~/.config/claude/` (Linux) |
| Cursor | `~/.cursor/mcp.json` or `.cursor/mcp.json` |
| Windsurf | `%USERPROFILE%\.codeium\windsurf\mcp_config.json` |
| Zed | `settings.json`, under `context_servers` |
| Goose | `~/.config/goose/config.yaml` |

```json
{
  "mcpServers": {
    "groundkit": {
      "command": "ck",
      "args": ["mcp"]
    }
  }
}
```

Zed nests the command (`"command": { "path": "ck", "args": ["mcp"] }`)
and Goose uses YAML with `type: stdio`. Installed as a .NET tool, the server is
also available as `groundkit-mcp`, which takes the same options.

For an HTTP server, start ContextKit with `ck mcp --http 4000` and configure
the MCP host with `http://127.0.0.1:4000/mcp`:

```json
{
  "mcpServers": {
    "groundkit": {
      "type": "streamable-http",
      "url": "http://127.0.0.1:4000/mcp"
    }
  }
}
```

## Restrict a session

`--libs` pins the server to specific installed packages, which is useful when
many packages are installed globally:

```bash
ck mcp -l react,vite      # every installed version of each
ck mcp -l 'nextjs@16.0'   # exactly one version
```

## Host-neutral contract

The integration only needs three facts:

- Command: starts the ContextKit MCP server (`ck mcp`).
- Transport: `stdio` or Streamable HTTP at `/mcp`.
- Tools: `query-docs`, `ask-docs`, `search-docs`, `find_libraries`, `get_docs`, `resolve-source`, `library_catalog`, `search_packages`, and `download_package`.

After connection, the recommended sequence is [resolve, search, download, query](/guide/mcp).
