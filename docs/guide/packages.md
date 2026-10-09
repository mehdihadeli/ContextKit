# Package workflow

Packages are versioned evidence artifacts. Build or import the package that matches the dependency version used by the project; do not combine multiple versions in one lookup workflow without making that choice explicit.

The repository also includes a declarative starter registry in `registry/`. Its YAML files describe source locations for the curated catalog, and a definition may list several versions, each with an explicit `tag`. `ck registry validate` checks those files and `build`/`build-all` produce one package per declared version - `registry/npm/angular.yaml` publishes `21.0.3`, `20.3.15`, and `19.2.17`. There is no automatic upstream version discovery: every version to publish is declared in the file.

## Two ways to get a package

Both paths end in the same local SQLite package, so querying is identical. Only
the origin differs.

**Published package from the registry (GHCR).** The default catalog on GitHub
Pages maps each package to a GHCR OCI manifest digest, its byte size, and its
SHA-256. The client resolves the digest, downloads the artifact's single layer,
verifies size and hash, and imports it. Reads are anonymous.

```bash
# Discovery, then an exact version
ck search-packages npm react
ck download-package npm react 19.1.0

# Local-first install: registry when it can serve the package
ck install npm/react
```

**Build from source.** `add` always builds, and `install` falls back to it when
the registry has no match or cannot be reached. For a curated name the catalog
entry supplies the GitHub repository and documentation path, which ContextKit
clones before indexing.

```bash
# Curated name expanded to a repository + docs path
ck add react

# Explicit source
ck add https://github.com/mattpocock/skills --tag v1.2.3
ck add ./my-library --path docs
```

A fallback build indexes the catalog repository's newest stable tag and records
the requested version as metadata only. Build explicitly with `--tag` when the
package must match a version exactly. Registry errors during download are
reported instead of retried as a local build, because a catalog entry that
cannot serve its artifact is broken rather than missing.

## Sources

The builder accepts local directories, Git repositories, `llms.txt` and `llms-full.txt` URLs, and arbitrary documentation pages.

For a website root, ContextKit probes `/llms-full.txt` first and then `/llms.txt`.
If the available `llms.txt` is an index rather than inlined documentation,
ContextKit follows its links and fetches the linked documents. A direct
`llms.txt` URL is also supported:

```bash
ck add https://agentgateway.dev
ck add https://agentgateway.dev/llms.txt
ck add https://agentgateway.dev --name agent-gateway
```

If neither llms endpoint exists, a website root falls back to fetching its page
directly. HTML pages use readable article extraction so navigation, subscription
calls to action, and comment widgets are not indexed. Raw Markdown URLs are
indexed directly, including GitHub blob URLs:

```bash
ck add https://agentgateway.dev/blog/2026-08-20-benchmarking-agentgateway-epp-proxy-overhead/
ck add https://github.com/agentgateway/agentgateway/blob/main/README.md --name agentgateway-readme
```

### Local directories

When the source is a local directory, ContextKit checks these folders in order:

1. `docs/`
2. `documentation/`
3. `doc/`
4. `website/docs/`
5. `guides/`
6. `guide/`
7. `manual/`
8. `reference/`
9. `content/`
10. `wiki/`

ContextKit selects the candidate containing the most supported documentation
files. If none contains documentation, it scans the repository root. Set
`--path` when documentation lives elsewhere:

```bash
ck add ./my-library
ck add ./my-library --path handbook
```

`--docs-path` remains available as an alias for `--path`. Relative paths resolve from the source directory; absolute paths are also supported.

If no supported documentation files are found, `add` creates the package with
zero documents and prints a warning containing a Perplexity search link. A
small package also receives a warning with the same suggestion because the
project may keep documentation in a separate repository, such as a
`project-docs`, `project.dev`, or `project-website` repository.

Local and Git builds also warn when every indexed document is a README, including
localized README filenames. This warning is independent of section count: a long
README can still omit dedicated guides or API reference. It does not reject the
package or reduce search scores, and it does not prove the documentation is incomplete.

### Git repositories

ContextKit accepts GitHub, GitLab, Bitbucket, and Codeberg repository URLs. GitHub tree URLs preserve their selected ref, and `--tag` explicitly selects a tag or branch:

```bash
ck add https://github.com/mattpocock/skills --tag main
```

Without an explicit ref, ContextKit chooses the latest stable tag when the remote exposes tags. Add `--choose-tag` for interactive selection.

### Package identity

By default, package ID and display name come from the source name. Override them and attach a version when publishing or maintaining multiple builds from one source:

```bash
ck add ./my-library --name my-library --pkg-version 1.0.0
```

The custom name is used for package and source identity; the custom version is stored in both package and source metadata and is shown by `inspect`.

Save a portable copy while also installing the package locally:

```bash
ck add ./my-library --path docs --name my-library --pkg-version 1.0.0 --save ./artifacts
```

`--save` accepts either a destination directory or a `.db` file path. A
directory receives the normal package filename, making the copy ready for
sharing or importing on another machine.

Host a saved database on an HTTP server and add its URL from another machine:

```bash
ck add https://github.com/mattpocock/skills --path docs \
	--name mattpocock-skills --pkg-version 1.2.3 \
	--save ./artifacts/mattpocock-skills@1.2.3.db

ck add https://packages.example.com/mattpocock-skills@1.2.3
```

Remote package URLs may use either the `.db` filename or a hosted
`name@version` path. ContextKit imports the artifact into its local package
store; subsequent queries do not contact the host.

The same artifact can be imported from the local filesystem:

```bash
ck add ./mattpocock-skills@1.2.3.db
```

During ingestion, ContextKit records source kind, canonical source ID, location, optional version, tag or branch, fingerprint, build timestamps, document counts, chunk counts, and warnings. Use `inspect` to review this provenance, including the persisted warning count. Warnings describe observed conditions such as README-only content, low section counts, empty documents, and auto-detected documentation paths; they are signals, not proof that documentation is incomplete.

### How sections are split

A package is split into sections at Markdown headings, and each section keeps the
heading path that leads to it. A `### Auth` inside a `## Middleware` is stored as
`Middleware > Auth`, not as a bare `Auth`, so two sections that share a name stay
distinguishable — in a result list, and to any later step that wants to join
neighbouring sections together. The H1 is left out of the path because it is
already the document title.

Headings inside a fenced code block are ignored, so a `#` comment in a shell or
Python sample never becomes a section. The `hasCode` flag on each section is set
from the fences it actually contains.

Both backtick and tilde fences are recognized. A closing fence must use the same
marker, be at least as long as the opening marker, and contain no trailing code.
Heading-only sections are omitted. Breadcrumbs retain the innermost three headings
below H1, and sibling headings reset the ancestor path. Existing installed artifacts
keep their old chunks until rebuilt; no package schema migration is required.

## Portable artifacts

Export a package for another machine:

```bash
ck export react ./artifacts
```

Import it into the local store:

```bash
ck import ./artifacts/react@dev.db
```

Remove an installed package with a bare name when only one version is present,
or target an exact version with `name@version`:

```bash
ck remove mattpocock-skills
ck remove mattpocock-skills@1.2.3
ck remove agentgateway
```

When several versions are installed, ContextKit asks you to choose a version in
an interactive terminal. Non-interactive removal lists the installed versions
and deletes nothing until an exact version is supplied.

## Refresh

Refresh from stored source metadata after documentation changes:

```bash
ck refresh react
```

Refresh rebuilds the package from its stored source metadata. It does not make a local query live or guarantee that an upstream branch has not changed. For reproducible grounding, prefer an exact Git tag or dependency version, then export the resulting `.db` artifact for teammates or CI.

## Evidence workflow

```bash
ck inspect react
ck query react "useEffect cleanup"
```

`query` also works before `inspect`: when the package is not installed and the
registry publishes it, `query` downloads it first. Use `--no-install` when a
command must not touch the network.

Check package identity, version, source, and build time before treating a result as authoritative. An empty query result means the package did not provide evidence for that query; it does not prove that the documented feature does not exist.
