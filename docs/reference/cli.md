# CLI reference

Run commands with `ck <command>`. The installed .NET tool registers the
short name `ck`, and the repository launchers answer to both names.

## Short aliases

ContextKit uses Spectre.Console.Cli for command parsing, so commands and common
options also support short names.

Run `ck --help` to see the same aliases, descriptions, and examples in
the built-in CLI help.

| Command            | Short alias(es) |
| ------------------ | --------------- |
| `add`              | `a`             |
| `import`           | `im`            |
| `export`           | `ex`, `exp`     |
| `list`             | `l`, `ls`       |
| `inspect`          | `ins`, `info`   |
| `query`            | `q`             |
| `refresh`          | `rf`, `ref`     |
| `remove`           | `rm`, `del`     |
| `catalog`          | `c`, `cat`      |
| `search-packages`  | `sp`, `search`  |
| `download-package` | `dp`, `dl`      |
| `install`          | `i`             |
| `mcp`              | -               |
| `registry`         | -               |
| `semantic`         | -               |

| Option          | Short alias |
| --------------- | ----------- |
| `--path`        | `-p`        |
| `--name`        | `-n`        |
| `--pkg-version` | `-v`        |
| `--save`        | `-s`        |
| `--tag`         | `-t`        |
| `--choose-tag`  | `-c`        |
| `--libs`        | `-l`        |
| `--urls`        | `-u`        |
| `--registry-url` | -          |

`--verbose` enables informational logs on standard error. Without it, the CLI
only emits error-level logs; command results remain on standard output.

During development, this repository ships repo-local `ck` and `groundkit`
launchers.
Add the repository root to `PATH` once per shell session so the examples below
run against the current source tree:

```bash
export PATH="$PWD:$PATH"
ck list
```

```powershell
$env:Path = "$PWD;$env:Path"
ck list
```

## Search modes

`query --search-mode <mode>` overrides the configured mode for one query:

| Value | Retrieval | Setup |
| --- | --- | --- |
| `lexical` | SQLite FTS5/BM25 for identifiers, keywords, and quoted phrases | None; default |
| `semantic` | Local embedding similarity for paraphrases | Provider and model |
| `hybrid` | Reciprocal-rank fusion of BM25 and embedding results | Provider and model |

```bash
ck query react "useEffect cleanup" --search-mode lexical
ck query react "stop work when a component disappears" --search-mode semantic
ck query "useEffect cleanup for background work" --libraries react --search-mode hybrid
```

Omit `--search-mode` to use the saved default, initially lexical. Installing assets
does not change that default. Semantic retrieval does not change library detection
or package version selection. Quoted phrase constraints apply to lexical matches,
not embedding matches. See the [Search mode guide](/guide/grounding#choosing-a-search-mode)
for mode selection and troubleshooting.

## Optional Semantic Setup

The main `ContextKit` NuGet tool does not include ONNX Runtime or model weights.
Download optional assets without installing another global tool:

```bash
ck semantic provider install onnx
ck semantic model list
ck semantic model install bge-micro-v2
ck semantic enable --model bge-micro-v2 --mode hybrid
ck semantic status
```

`enable` tests embedding generation and indexes installed packages before saving
the default. Only `semantic` and `hybrid` are accepted by `--mode`; omitting it
selects hybrid. To save an embedding-only default, run
`ck semantic enable --model bge-micro-v2 --mode semantic`. Use
`ck semantic disable` to restore lexical search; assets and caches are retained.
CLI and MCP hosts read the same settings on subsequent queries.

```bash
ck semantic index react             # prepare one installed package/version
ck semantic index                   # prepare all installed packages
ck semantic model use bge-micro-v2   # select a verified installed model
ck query react "component teardown" --search-mode hybrid
ck query react "useEffect" --search-mode lexical
```

Only the pinned quantized `bge-micro-v2` profile is currently supported: English,
384 dimensions, 512-token model limit, MIT license, 17,642,355 downloaded bytes
including vocabulary and license. Assets are verified against pinned SHA-256
values. Package or model/profile changes generate new sidecars rather than mixing
incompatible vectors. Old caches are retained.

Settings, providers, models, and indexes live under `GROUNDKIT_HOME/semantic`, or
`~/.groundkit/semantic` if unset. Queries never download provider/model assets;
missing setup fails with guidance. Building a missing index uses local inference
and can make the first semantic query slower. Vector lookup is a linear scan
suitable for modest documentation packages, not an ANN index.

Provider archives and checksums come over HTTPS from the ContextKit GitHub release
matching the tool version and OS/architecture. Supported release targets:
`win-x64`, `linux-x64`, `osx-x64`, `osx-arm64`. That release must publish provider
assets before remote installation works. Workers reuse .NET 10; no second .NET
runtime is bundled.

For source development or offline transfer, build a provider bundle and import
its explicitly trusted directory. The bundle contains executable code: a checksum
checks integrity, not publisher identity. Only import bundles you trust.

Packaging requires Bash, the .NET 10 SDK, `jq`, `zip`, and GNU coreutils
(`sha256sum`, `sort`, `wc`), available on the Ubuntu release runner.

```bash
bash scripts/package-semantic-provider.sh --runtime win-x64 --output ./artifacts
ck semantic provider install onnx --bundle ./artifacts/semantic-provider/win-x64
```

The script emits a RID-specific zip, external JSON manifest, and bundle directory.
Release publishing uploads zip/manifest alongside CLI executables. File/archive
checksums, protocol, and OS/architecture are checked before activation; failed
downloads preserve previous assets. Model installation requires network access
unless its verified cache has been transferred separately.

## Add sources

`add` accepts a local directory, Git repository URL, `llms.txt` URL, or arbitrary documentation URL.

| Option                    | Purpose                                                                                                      |
| ------------------------- | ------------------------------------------------------------------------------------------------------------ |
| `--path <path>`           | Documentation directory inside a local directory or repository. Relative paths resolve from the source root. |
| `--docs-path <path>`      | Backward-compatible alias for `--path`.                                                                      |
| `--name <name>`           | Override generated package ID and display name.                                                              |
| `--pkg-version <version>` | Set package and source version metadata.                                                                     |
| `--tag <tag>`             | Build a Git repository at a specific tag or ref.                                                             |
| `--choose-tag`            | Interactively choose a Git tag when the terminal supports prompts.                                           |
| `--save <path>`           | Copy the installed package to a file or directory for sharing.                                               |

Examples:

```bash
# Detect common documentation folders automatically
ck add ./my-library

# Use an explicit documentation directory
ck add /path/to/repo --path docs

# Set package identity and version
ck add ./my-library --name my-library --pkg-version 1.0.0

# Save a shareable copy while installing locally
ck add ./my-library --name my-library --pkg-version 1.0.0 --save ./artifacts

# Build a repository at a tag
ck add https://github.com/mattpocock/skills --tag v1.0.0
```

Website documentation can be loaded from an automatically discovered or direct
`llms.txt` endpoint:

```bash
# Auto-fetch llms-full.txt or llms.txt
ck add https://agentgateway.dev

# Fetch this llms.txt index and follow its linked documents
ck add https://agentgateway.dev/llms.txt

# Override the generated package name
ck add https://agentgateway.dev --name agent-gateway

# Fall back to readable extraction for an article without llms.txt
ck add https://agentgateway.dev/blog/2026-08-20-benchmarking-agentgateway-epp-proxy-overhead/

# Fetch raw Markdown from a GitHub blob URL
ck add https://github.com/agentgateway/agentgateway/blob/main/README.md --name agentgateway-readme
```

For website roots, ContextKit probes `/llms-full.txt` and then `/llms.txt`. If
neither endpoint exists, it fetches the URL directly. HTML is reduced to
readable article content; Markdown and other text responses are indexed as-is.

Packages can also be shared as hosted SQLite artifacts. Build and save a
package, place the `.db` file on an HTTP server, then add its URL. The URL may
end in `.db` or use a `name@version` path:

```bash
ck add https://github.com/mattpocock/skills --path docs \
	--name mattpocock-skills --pkg-version 1.2.3 \
	--save ./artifacts/mattpocock-skills@1.2.3.db

ck add https://packages.example.com/mattpocock-skills@1.2.3
```

To add a saved database from the local filesystem, pass its `.db` path directly:

```bash
ck add ./mattpocock-skills@1.2.3.db
```

For Git repository URLs without `--tag`, ContextKit selects the latest stable tag when tags are available. `--choose-tag` changes this to an interactive selection.

| Command                                                                                  | Purpose                                                                                  |
| ---------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| `add <source> [--path <path>] [--name <name>] [--pkg-version <version>] [--save <path>]` | Build and install a package; local directories auto-detect the documented common folders |
| `catalog [query]`                                                                        | Browse curated sources                                                                   |
| `list`                                                                                   | List installed packages                                                                  |
| `query <package-id> <topic>`                                                             | Search documentation, downloading the package first when it is published                |
| `query "<question>"`                                                                     | Detect the package from the question, then search or download it                        |
| `query "<question>" --libraries <a,b>`                                                   | Search several named packages for one question and merge the hits                        |
| `inspect <package-id>`                                                                   | Inspect package and source metadata                                                      |
| `refresh <package-id>`                                                                   | Rebuild from stored source metadata                                                      |
| `import <package-file>`                                                                  | Install a SQLite package                                                                 |
| `export <package-id> <destination>`                                                      | Copy a package artifact                                                                  |
| `search-packages <registry> <name> [version]`                                            | Search hosted registry                                                                   |
| `download-package <registry> <name> <version>`                                           | Download and install package                                                             |
| `install <registry/name\|name\|source> [version]`                                        | Install from the registry, else build from source                                        |
| `remove <name[@version]>`                                                                | Remove one installed package version                                                     |
| `mcp [--libs <names>] [--http [port]] [--host <host>]`                                  | Start the stdio or Streamable HTTP MCP server                                            |
| `registry <subcommand> [options]`                                                        | Maintainer tooling for definitions, builds, and OCI publication                           |

`list` renders a package inventory table with package name, version, size, documents, sections, and a totals summary.

### Query documentation

`--search-mode lexical|semantic|hybrid` overrides the configured mode for one
query, including question-based and multi-library queries. Without an override,
queries use the configured mode, defaulting to lexical BM25. Semantic and hybrid
modes require explicit provider/model setup above.

`query` returns the same focused sections as the MCP tools, from the local
package store. It works on installed packages and needs no network access for
them:

```bash
# Default output is machine-readable JSON, one object per query
ck query react "useEffect cleanup"

# Exact installed version
ck query 'angular@20.3.15' 'component lifecycle'

# A partial version resolves to the newest matching one: this uses 19.2.17
ck query 'angular@19' 'component lifecycle'

# Rendered panels instead of JSON, for a human reader
ck query react "useEffect cleanup" --pretty
```

Ask a plain question instead of naming the package, and ContextKit detects the
package from the question:

```bash
# The mention of "axios" is enough; the package is downloaded once, then queried
ck query "how do i add request interceptors in axios?"
```

Pass one or more library hints to search several packages for the same question.
Hints override detection and accept a name, an id, or a `registry/name@version`
selector, up to four:

```bash
# The question names no library, so only the hints decide what is searched
ck query "how do i stream a response" --libraries openai,next

# A hinted version is resolved like any other, so this uses the newest installed 19.x
ck query "component lifecycle" --libraries angular@19
```

Hints are resolved installed-first, so a local copy answers instead of a download.
Hits from every library are merged by rank and each hit reports the package it came
from. See [Question-based search](/guide/question-search).

Detection checks installed packages first, then the curated catalog, so a local
copy always wins over a download. It matches the words of the question, not just
the exact id: `next.js` and `Next JS` are the same name, common aliases such as
`angularjs` and `nest` are recognised, a package named in the middle of a
sentence still wins on its whole name, and a small typo such as `angualr` is
forgiven. An explicitly named package always beats a near miss, so
`next steps in angualr` is read as `next`. If the question names no known
package, the command exits with `1` and lists the installed packages plus the
`ck add` commands that would make one queryable.

A version does not have to be complete. `angular@19` matches `19.2.17`, `19.2`
matches the newest `19.2.x`, and `^19` / `v19` are read the same way. The rules
are narrow on purpose: a request only matches on whole version segments, so `19`
never resolves to `190.0.0`, and a request that matches nothing resolves to
nothing instead of falling back to an unrelated line.

Resolution runs in the same order every time:

1. The local store. A partial version is matched against the installed versions
   first, so an installed `19.2.17` answers `angular@19` with no network access.
2. The registry. A version that is not installed but is published is downloaded
   and then queried.
3. Nothing. The command exits with `1` and explains both routes that would fix it.

When a selector is answered from the local store or resolved to a different
version, the substitution is printed so the answer is never ambiguous:

```text
$ ck query angular@19 "component lifecycle"
Using installed angular@19.2.17 for angular@19.

$ ck query angular@19 "component lifecycle" --no-install
Version 19 of npm/angular resolves to 19.2.17.
```

If the package is not installed, `query` does not stop there. It looks the
package up in the registry and downloads it before searching, so one command is
enough for a library ContextKit publishes:

```text
$ ck query vue "composition api"
Package vue is not installed. Downloading npm/vue@latest from the registry...
Installed package: ~/.groundkit/packages/vue@latest.db
{"packageId":"vue","version":"latest","totalTokens":140,"hits":[...]}
```

This is registry-only. A package that is not published is never cloned from
source, because building a package from a repository is too large a side effect
for a read command. Instead the command exits with `1` and names the two routes
that would fix it: install the published artifact, or build it once from a
repository or a local docs folder. A library in the curated catalog also gets a
ready-to-run `add` command:

```text
$ ck query left-pad "install"
Package left-pad is not installed.
npm/left-pad was not found in the registry.
Download it with ck install npm/left-pad, then query again.
Or build the package once from a repository or a local docs folder, then query it offline:
  ck add <repository-url> --docs-path <folder> --name left-pad
  ck add ./left-pad --docs-path <folder> --name left-pad
Installed packages: axios, vue
```

```text
$ ck query angular@18 "component lifecycle"
Package angular@18 is not installed.
Version 18 of npm/angular is not published. Published versions: 21.0.3, 20.3.15, 19.2.17.
Download it with ck install npm/angular 21.0.3, then query again.
Or build the package once from a repository or a local docs folder, then query it offline:
  ck add https://github.com/angular/angular --docs-path adev/src/content --name angular
  angular is in the curated catalog (Web application framework).
```

Pass `--no-install` to keep `query` strictly offline. The registry lookup and
download are then skipped and the guidance is printed instead, which is what a
sandboxed or audited environment wants.

| Option          | Purpose                                                                                        |
| --------------- | ---------------------------------------------------------------------------------------------- |
| `--pretty`      | Render each hit as a panel instead of JSON.                                                    |
| `--no-install`  | Never download. Report what is missing and how to add it.                                      |
| `--registry-url <URL>` | Use a different catalog for the missing-package lookup, for this invocation only.       |

A selector may be a bare name, `registry/name`, or either form with `@version`.
A bare name targets the npm registry, matching `install`, and a leading `@`
means a scoped package name such as `@angular/core`, not a registry prefix.

### Remove packages

Remove one installed package version by name or by an exact `name@version`
selector:

```bash
ck remove mattpocock-skills
ck remove mattpocock-skills@1.2.3
ck remove agentgateway
```

A bare name is accepted only when one version of that package is installed. If
multiple versions are installed, an interactive terminal lists the versions and
asks you to choose one. Non-interactive runs list the installed versions and
exit without deleting anything, so removal never guesses.

### Install a package

`install` obtains one package and installs it locally. It tries the published
registry package first and falls back to building from source.

**Path A: prebuilt package from the registry.** The default catalog is a static
`index.json` on GitHub Pages that maps each package to a GHCR OCI manifest
digest, its byte size, and its SHA-256. `install` resolves the digest, downloads
the artifact's single layer, verifies size and hash, and imports it. Reads are
anonymous, so no credential is required.

```bash
# Bare names default to the npm registry
ck install react

# Exact published version
ck install npm/react 19.1.0

# Multi-version packages need an explicit version
ck install npm/angular 20.3.15
```

**Path B: build from source.** Used when the registry has no match or cannot be
reached. A curated name is expanded to its catalog repository and documentation
path and cloned; a local folder, Git repository URL, `llms.txt` site, or raw page
is used as supplied.

```bash
# Fallback for a curated name: clones the catalog repository instead of downloading
ck install react

# Explicit source instead of a registry name
ck install https://github.com/mattpocock/skills
```

Resolution order:

1. Reuse a matching package already in the local store. No network request.
2. Search the configured registry and import the matching artifact.
3. Registry has no match or is unreachable: build from the catalog entry or the supplied source.

Installing is idempotent: a package is stored under its own identity and
version, so installing the same `name` and `version` again replaces that single
entry instead of adding a second copy.

### Serve MCP

`mcp` starts the MCP server. stdio is the default; `--http` switches to
Streamable HTTP at `/mcp`.

```bash
ck mcp                                    # stdio
ck mcp --http                             # http://127.0.0.1:4000/mcp
ck mcp --http 4000 --host 0.0.0.0         # bind all interfaces
ck mcp -l react,vite                      # restrict the session
ck mcp http --urls http://localhost:4000  # explicit URL form
```

| Option | Purpose |
| --- | --- |
| `--libs <names>` | Expose only the listed packages. A bare name covers every installed version; `name@version` covers one. |
| `--http [port]` | Serve Streamable HTTP instead of stdio. Default port 4000. |
| `--host <host>` | Host to bind. Default `127.0.0.1`. |
| `--urls <url>` | Explicit URL form, used with the `http` subcommand (alias `h`). |

The server reads only packages already in the local store. Registry access
happens in the CLI, before the server starts.

A registry error while downloading a matched package is reported instead of
falling back, because a catalog that answers but cannot serve its artifact is a
broken entry rather than a missing package. `download-package` is the strict
registry-only path with no fallback at all; `add` always builds from source and
is the command that supports `--tag` for a reproducible build.

A fallback build indexes the catalog repository's newest stable tag and only
records the requested version as package metadata. When the local build must
match a version exactly, build it explicitly with `ck add --tag`.

`--registry-url <URL>` applies to `install`, `query`, `search-packages`, and
`download-package` for the current invocation only. `RegistryUrl` in
`.groundkit/config.json` and `GROUNDKIT_REGISTRY_URL` set it persistently. The
default endpoint is `https://mehdihadeli.github.io/groundkit/registry/index.json`.

### Documentation discovery warnings

When `add` finds no supported documentation files, it prints `No documentation
content was found` and includes a Perplexity search URL for finding the
appropriate documentation repository. When only a few sections are indexed,
it prints a similar warning. This is useful for projects whose source and
documentation are maintained in separate repositories, for example:

```text
ck add https://github.com/facebook/react
Warning: Only a few sections were indexed. Search for the appropriate documentation repository: <Perplexity URL>

ck add https://github.com/reactjs/react.dev
```
