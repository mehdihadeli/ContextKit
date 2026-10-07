# Architecture

GroundKit separates expensive ingestion from the fast retrieval path.

## Project boundaries

The solution is organized around dependency direction rather than one project per
technical noun:

```text
src/
├── GroundKit.Abstractions/       # Shared records, options, and service interfaces
├── GroundKit.Core/               # Ingestion, source detection, package workflows
├── GroundKit.Storage.Sqlite/     # SQLite persistence, FTS5, and BM25 retrieval
├── GroundKit.Hosting/            # DI composition for executable hosts
├── GroundKit.Cli/                # User-facing command-line tool
├── GroundKit.Mcp/                # MCP stdio and HTTP adapter
├── GroundKit.Registry/          # Registry maintenance and package tooling
└── GroundKit.ServiceDefaults/    # Host telemetry, health checks, and resilience
```

The dependency direction is:

```mermaid
flowchart LR
  abstractions[GroundKit.Abstractions]
  core[GroundKit.Core]
  storage[GroundKit.Storage.Sqlite]
  hosting[GroundKit.Hosting]
  cli[GroundKit.Cli]
  mcp[GroundKit.Mcp]
  registry[GroundKit.Registry]
  defaults[GroundKit.ServiceDefaults]

  core --> abstractions
  storage --> abstractions
  hosting --> core
  hosting --> storage
  cli --> hosting
  mcp --> hosting
  registry --> hosting
  registry -. uses .-> defaults
```

`GroundKit.Abstractions` has no SQLite, EF Core, MCP, or CLI dependency. SQLite
entities remain internal to `GroundKit.Storage.Sqlite`; they are not a public
schema package. MCP payloads remain MCP adapter contracts rather than leaking
into the core domain.

## High-level architecture

GroundKit has two main paths: an ingestion path that creates portable SQLite
packages, and a retrieval path that serves those packages to an AI client.

```mermaid
flowchart TB
  sources[Git repositories, folders, websites, llms.txt]
  cli[GroundKit CLI]
  ingestion[Core ingestion and package builder]
  local[(Portable SQLite package)]
  registry[Registry API]
  hosted[(Hosted SQLite artifacts)]
  mcp[MCP stdio or HTTP host]
  ai[AI client]

  sources --> cli
  cli --> ingestion
  ingestion --> local
  cli -->|search or download| registry
  registry --> hosted
  hosted -->|import or download| local
  local --> mcp
  ai <-->|MCP tools and results| mcp
```

## Component interactions

The common workflows connect the adapters to the same core and storage
services. HTTP hosts also expose health endpoints and use `ServiceDefaults`;
stdio and CLI processes remain local console applications.

```mermaid
sequenceDiagram
  participant Source as Documentation source
  participant CLI as GroundKit CLI
  participant Core as Core ingestion
  participant Store as SQLite package store
  participant Registry as Registry API
  participant MCP as MCP host
  participant AI as AI client

  Source->>CLI: add source
  CLI->>Core: detect, load, and build documents
  Core->>Store: persist package and FTS index
  CLI-->>Source: report package result

  AI->>MCP: query(package, topic)
  MCP->>Store: search FTS5 and rank with BM25
  Store-->>MCP: matching sections and metadata
  MCP-->>AI: MCP response with evidence

  CLI->>Registry: search or download package
  Registry-->>CLI: package metadata or SQLite artifact
  CLI->>Store: import downloaded artifact
```

## Host defaults and logging

`GroundKit.Hosting` provides shared dependency-injection composition through
`AddGroundKitServices()`. It does not configure logging or OpenTelemetry, so it
can be used by both short-lived command-line processes and long-running hosts.

`GroundKit.ServiceDefaults` is used by the Registry host, not by the CLI. The
Registry exposes an HTTP API and has a service lifecycle, so it benefits from
the default health checks, HTTP resilience, service discovery, and
OpenTelemetry configuration provided by `AddServiceDefaults()`.

The CLI uses the console logger directly. Errors are emitted at the default
level, while `groundkit --verbose <command>` enables informational logs. Logs
go to standard error so command results, including query JSON, remain clean on
standard output. OpenTelemetry is intentionally opt-in for the CLI because a
one-shot local process normally has no collector or service-level telemetry
destination.

Tests follow the same ownership boundaries: `GroundKit.Core.Tests`,
`GroundKit.Storage.Sqlite.Tests`, `GroundKit.Cli.Tests`,
`GroundKit.Mcp.Tests`, and `GroundKit.Registry.Tests`.

Protocol, client, SDK, and server packages are intentionally deferred. They
should be introduced only when an API has independent consumers, release
versioning, or a deployment lifecycle separate from the current host.

```mermaid
flowchart LR
  A[Git, folder, URL, llms.txt] --> B[Ingestion]
  B --> C[SQLite package]
  C --> D[FTS5 and BM25]
  D --> E[MCP tools]
  E --> F[AI agent]
```

## Build phase

The ingestion layer detects the source, loads supported documents, extracts sections, estimates tokens, computes fingerprints, and creates a `BuildResult`.

The storage layer persists manifests, source metadata, documents, chunks, warnings, and an FTS5 search table in one portable `.db` file.

## Query phase

The MCP server opens the selected package, builds an FTS query, ranks matches with BM25, applies a relative relevance cutoff, and trims results to the requested token budget.

Queries do not fetch the internet. Registry access is only needed when discovering or downloading a package.

The result includes package identity and version, selected document and section titles, content, token estimates, code presence, and relevance scores. This makes retrieved evidence inspectable by both an MCP client and a human reviewing an agent response.

## Data boundaries

GroundKit is a static documentation grounding layer. It is appropriate for versioned API references, guides, runbooks, and other documents that can be packaged. It is not a source of live operational truth: prices, inventory, feature flags, and service status belong behind a current API or database query.

## Why SQLite

Library documentation is curated, structured, and queried with concrete API names. Full-text search gives inspectable exact-term matching without embedding generation, vector infrastructure, or a remote query dependency. A vector index can be added for a corpus with large scale, weak vocabulary overlap, or unstructured content, but it is not required by this documentation use case.
