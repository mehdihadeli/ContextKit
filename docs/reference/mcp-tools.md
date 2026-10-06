# MCP tools

## `query-docs`

Primary documentation lookup. Takes `packageId`, `topic`, `maxTokens`, `maxHits`, and `relativeScoreCutoff`.

## `get_docs`

Compatibility alias for `query-docs`. Takes `library`, `topic`, `maxTokens`, `maxHits`, and `relativeScoreCutoff`.

## `resolve-source`

Finds installed packages by package ID or display name.

## `library_catalog`

Lists the 19 curated starter sources, optionally filtered by name or description.

## `search_packages`

Searches a compatible registry by `registry`, `name`, and optional `version`.

## `download_package`

Downloads a registry package, imports it into the local store, and makes it available to `query-docs` and `get_docs`.

All documentation results include structured content for MCP-aware clients and concise text for agents that consume tool output as prose.
