---
title: Features
titleTemplate: Local-first documentation workflows
description: See how ContextKit builds, searches, connects, and shares versioned documentation packages.
---

<div class="feature-page">

<div class="feature-hero">
  <div class="feature-eyebrow">GROUNDKIT / CAPABILITIES</div>
  <h1>Ground truth for your AI workflow</h1>
  <p class="feature-lede">Versioned documentation packages for agents that need exact APIs, private knowledge, and repeatable evidence.</p>
  <div class="feature-stats">
    <span><strong>SQLite</strong> portable packages</span>
    <span><strong>FTS5</strong> full-text retrieval</span>
    <span><strong>stdio</strong> and <strong>HTTP</strong> MCP transports</span>
  </div>
</div>

<nav class="feature-jump" aria-label="Feature sections">
  <a href="#build-once">Build once</a>
  <a href="#search-locally">Search locally</a>
  <a href="#connect-anywhere">Connect anywhere</a>
  <a href="#share-with-teams">Share with teams</a>
</nav>

<div id="build-once" class="feature-section">
  <div class="feature-copy">
    <div class="feature-number">01 / BUILD ONCE</div>
    <h2>Documentation becomes an artifact</h2>
    <p>Ingest a Git repository, local folder, llms.txt source, or raw page. The builder extracts sections, records provenance, estimates tokens, and produces one portable SQLite package.</p>
    <ul class="feature-list">
      <li>Local and private sources stay in your environment</li>
      <li>Fingerprints make source changes visible</li>
      <li>Packages can move with a project or team</li>
    </ul>
  </div>
  <div class="feature-panel">
    <div class="panel-label">BUILD A PACKAGE</div>
    <div class="feature-code">$ ck add react<br><br>OK source detected: git<br>OK sections extracted: 1,842<br>OK package: react@19.1.0.db</div>

  </div>
</div>

<div id="search-locally" class="feature-section feature-section-reverse">
  <div class="feature-copy">
    <div class="feature-number">02 / SEARCH LOCALLY</div>
    <h2>Answers start with the right version</h2>
    <p>SQLite FTS5 and BM25 rank focused documentation sections without embeddings or a remote query service. Token budgets and relevance cutoffs keep results useful to an agent.</p>
    <ul class="feature-list">
      <li>Exact terms and API names work naturally</li>
      <li>Deterministic input and bounded output</li>
      <li>Source metadata stays inspectable</li>
    </ul>
  </div>
  <div class="feature-panel feature-result">
    <div class="panel-label">QUERY RESULT</div>
    <div class="query-line"><span class="terminal-prompt">&gt;</span> useEffect cleanup</div>
    <div class="result-line"><span class="result-rank">01</span><span><strong>Effect cleanup</strong><small>react.dev/reference/react/useEffect</small></span><b>0.94</b></div>
    <div class="result-line"><span class="result-rank">02</span><span><strong>Synchronizing with effects</strong><small>react.dev/learn/synchronizing-with-effects</small></span><b>0.81</b></div>
  </div>
</div>

<div id="connect-anywhere" class="feature-section">
  <div class="feature-copy">
    <div class="feature-number">03 / CONNECT ANYWHERE</div>
    <h2>One MCP server. Every host.</h2>
    <p>Expose the local package store through standard stdio MCP. Editors and agent hosts can resolve sources, retrieve docs, and discover packages without a custom integration.</p>
    <ul class="feature-list">
      <li>Works with MCP-compatible clients</li>
      <li>Structured results with deterministic text formatting</li>
      <li>No cloud account or API key required</li>
    </ul>
  </div>
  <div class="feature-panel">
    <div class="panel-label">MCP CONFIGURATION</div>
    <div class="feature-code">{<br>&nbsp;&nbsp;"servers": {<br>&nbsp;&nbsp;&nbsp;&nbsp;"groundkit": {<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;"type": "stdio",<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;"command": "ck",<br>&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;&nbsp;"args": ["mcp"]<br>&nbsp;&nbsp;&nbsp;&nbsp;}<br>&nbsp;&nbsp;}<br>}</div>
  </div>
</div>

<div id="share-with-teams" class="feature-section feature-section-reverse">
  <div class="feature-copy">
    <div class="feature-number">04 / SHARE WITH TEAMS</div>
    <h2>Private knowledge, packaged</h2>
    <p>Ship internal runbooks, service contracts, and design-system notes alongside a project. Everyone queries the same artifact, while proprietary content remains under your control.</p>
    <ul class="feature-list">
      <li>Export and import SQLite artifacts</li>
      <li>Use a local or internal registry</li>
      <li>Refresh from stored source metadata</li>
    </ul>
  </div>
  <div class="feature-panel feature-callout">
    <div class="panel-label">THE RESULT</div>
    <blockquote>"How do I configure the staging payments service?"</blockquote>
    <div class="callout-answer"><span class="terminal-ok">SOURCE FOUND</span> internal runbook / staging.md</div>
    <div class="callout-answer"><span class="terminal-ok">LOCAL ONLY</span> query did not leave this machine</div>
  </div>
</div>

<div class="feature-cta">
  <div>
    <div class="feature-eyebrow">READY TO CONNECT</div>
    <h2>Give your agent better evidence.</h2>
  </div>
  <a href="/guide/quickstart" class="feature-button">Start with quickstart <span>-&gt;</span></a>
</div>

</div>
