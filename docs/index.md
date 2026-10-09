---
layout: home
title: ContextKit
titleTemplate: Local-first documentation grounding
description: Build versioned documentation packages once. Give AI agents precise local context through MCP.
hero:
  name: ContextKit
  text: Documentation that stays close to your code
  tagline: Build versioned docs into portable SQLite packages. Query them through MCP with no cloud dependency on the hot path.
  actions:
    - theme: brand
      text: Start building
      link: /guide/quickstart
    - theme: alt
      text: See the architecture
      link: /guide/architecture
features:
  - icon: "01"
    title: Build once
    details: Ingest Git repositories, local folders, llms.txt sites, or raw pages into searchable packages.
  - icon: "02"
    title: Query locally
    details: SQLite FTS5 and BM25 return focused sections with token budgets and relevance controls.
  - icon: "03"
    title: Ground agents
    details: Expose documentation through standard MCP tools with structured results and deterministic text formatting.
---

<div class="home-note">
  <strong>Core promise:</strong> exact library version, local source, inspectable retrieval.
</div>

## Install

Build a package once, store it locally, then query it from your agent. Nothing
on the query path calls a hosted documentation service.

```bash
dotnet tool install --global ContextKit   # installs the `ck` command
ck install npm/react              # prebuilt package from the registry
ck add ./my-library --path docs   # or build one from source
ck query react "useEffect cleanup"
```

## Choose your starting point

Start with the workflow that matches your next task. Each path leads to a concrete action instead of a product tour.

<div class="home-paths">
  <a href="/libraries" class="home-path">
    <strong>Find a library</strong>
    <span>Browse 19 curated sources and build one with a short command.</span>
  </a>
  <a href="/features" class="home-path">
    <strong>Understand the workflow</strong>
    <span>See how ingestion, retrieval, MCP, and team sharing fit together.</span>
  </a>
  <a href="/integrations" class="home-path">
    <strong>Connect an agent</strong>
    <span>Connect with stdio or Streamable HTTP from your MCP-compatible host.</span>
  </a>
</div>

## Made for agent workflows

Documentation lookup should disappear into the development loop. Add packages during project setup, ship them with a team workspace, or rebuild them in CI. The agent gets the same evidence every time.

<div class="home-grid">
  <a href="/features" class="home-grid-item">
    <span class="home-grid-kicker">WORKFLOWS</span>
    <strong>Coding assistants</strong>
    <span>Keep framework APIs aligned with the version in your project.</span>
  </a>
  <a href="/features" class="home-grid-item">
    <span class="home-grid-kicker">KNOWLEDGE</span>
    <strong>Private runbooks</strong>
    <span>Index internal Markdown and keep proprietary context on your machine.</span>
  </a>
  <a href="/features" class="home-grid-item">
    <span class="home-grid-kicker">AUTOMATION</span>
    <strong>CI pipelines</strong>
    <span>Use deterministic packages for review bots, migrations, and validation.</span>
  </a>
</div>

## One server, any MCP client

ContextKit speaks standard stdio MCP and Streamable HTTP. Connect it to your editor, agent host, or shared container, then use the same package store from interactive sessions and automation.

[Configure an integration](/integrations) <span class="home-arrow">-></span>
