# Integrations

## Connect any MCP-compatible host

GroundKit supports standard stdio and Streamable HTTP transports. Use stdio when an MCP host owns one server process; use HTTP when multiple clients share a server at `/mcp`.

## VS Code and Copilot

Add a workspace MCP configuration in `.vscode/mcp.json`:

```json
{
  "servers": {
    "groundkit": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "src/GroundKit.Mcp"]
    }
  }
}
```

Use the published executable in shared environments to avoid a build on every launch.

## Claude Desktop, Cursor, and other hosts

Most MCP clients accept a JSON server entry. Adapt the command shape to the host's configuration file:

```json
{
  "mcpServers": {
    "groundkit": {
      "command": "groundkit",
      "args": ["mcp"]
    }
  }
}
```

On Windows, use an absolute project path with forward or escaped backslashes. A published server can instead use `"command": "groundkit-mcp"`.

For an HTTP server, start GroundKit with `groundkit mcp --http 4000` and configure
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

## Host-neutral contract

The integration only needs three facts:

- Command: starts the GroundKit MCP server.
- Transport: `stdio` or Streamable HTTP at `/mcp`.
- Tools: `resolve-source`, `get_docs`, `library_catalog`, `search_packages`, and `download_package`.

After connection, the recommended sequence is [resolve, search, download, query](/guide/mcp).
