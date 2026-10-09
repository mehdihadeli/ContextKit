# MCP tools

## Search modes

`query-docs`, `get_docs`, `ask-docs`, and `search-docs` accept optional `searchMode`:

| Value | Retrieval | Requirements |
| --- | --- | --- |
| `lexical` | SQLite FTS5/BM25 | No semantic setup |
| `semantic` | Local embedding similarity | Installed ONNX provider and model |
| `hybrid` | Reciprocal-rank fusion of lexical and semantic results | Installed ONNX provider and model |

Omit `searchMode` to use the configured default, initially lexical. A provided mode
overrides that default without changing settings. Run semantic setup in the same
`GROUNDKIT_HOME` as the MCP host; see the [Search mode guide](/guide/grounding#choosing-a-search-mode).
These tools never install provider/model assets, and missing setup returns guidance
instead of falling back. `ask-docs` and `search-docs` may still download documentation
packages as described below.

Example `query-docs` arguments for an exact API topic:

```json
{
	"packageId": "react",
	"topic": "useEffect cleanup",
	"searchMode": "lexical",
	"maxTokens": 2000,
	"maxHits": 8
}
```

Example `search-docs` arguments after optional setup:

```json
{
	"question": "stop background work after component removal",
	"libraries": ["react"],
	"searchMode": "hybrid",
	"maxTokens": 2000,
	"maxHits": 8
}
```

Use `"searchMode": "semantic"` to try embedding-only retrieval. Library detection
still matches names and aliases; semantic retrieval does not infer a library from
an unnamed topic. Supply a package id or library hints for that case.

## `query-docs`

Primary documentation lookup. Takes `packageId`, `topic`, `maxTokens`, `maxHits`, `relativeScoreCutoff`, and optional `searchMode`.

If the package is not installed, the tool returns an actionable message instead of a bare failure: the packages it can see locally, the versions published in a registry, and the exact `ck install` command. It never downloads on its own.

## `ask-docs`

Ask a documentation question without naming a package: `question`, `maxTokens`, `maxHits`, `relativeScoreCutoff`, and optional `searchMode`. The package is detected from the question, preferring an installed copy; when the detected package is published it is downloaded once and then queried, so a single call answers the question. If the question names no known package, the response lists the installed packages and the `ck add` commands that would make one queryable.

## `find_libraries`

Resolve which libraries a question is about, before querying: `question`, optional `libraryName`, and `maxResults`. Returns library ids that `query-docs` accepts, ranked by relevance and preferring installed packages. Read-only and local-only: it never contacts a registry and installs nothing. Use it when the library is unclear or the question could span several. See [Question-based search](/guide/question-search).

## `search-docs`

Answer a question across one or more libraries in a single call: `question`, optional `libraries` (up to four names or ids), `maxTokens`, `maxHits`, `relativeScoreCutoff`, and optional `searchMode`. The question selects the libraries when no hints are given; hints override it. Installed packages are used first and a published package is downloaded once. Hits from every library are fused by reciprocal rank fusion and each hit carries the `packageId` it came from, so a cross-library answer stays attributable. This is the only other tool besides `ask-docs` that may install, and only the libraries it resolved.

## `get_docs`

Compatibility alias for `query-docs`. Takes `library`, `topic`, `maxTokens`, `maxHits`, `relativeScoreCutoff`, and optional `searchMode`. Returns the same not-installed guidance.

## `resolve-source`

Finds installed packages by package ID or display name. Read-only and local-only: it never contacts a registry.

## `library_catalog`

Lists the curated starter sources, optionally filtered by name or description.

## `search_packages`

Searches a compatible registry by `registry`, `name`, and optional `version`.

## `download_package`

Downloads a registry package, imports it into the local store, and makes it available to `query-docs` and `get_docs`. Re-downloading the same version replaces the single stored entry rather than adding a duplicate.

All documentation results include structured content for MCP-aware clients and concise text for agents that consume tool output as prose.
