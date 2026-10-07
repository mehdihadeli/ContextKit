# CLI reference

Run commands with `groundkit <command>`.

## Short aliases

GroundKit uses Spectre.Console.Cli for command parsing, so commands and common
options also support short names.

Run `groundkit --help` to see the same aliases, descriptions, and examples in
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

| Option          | Short alias |
| --------------- | ----------- |
| `--path`        | `-p`        |
| `--name`        | `-n`        |
| `--pkg-version` | `-v`        |
| `--save`        | `-s`        |
| `--tag`         | `-t`        |
| `--choose-tag`  | `-c`        |

`--verbose` enables informational logs on standard error. Without it, the CLI
only emits error-level logs; command results remain on standard output.

During development, this repository ships repo-local `groundkit` launchers.
Add the repository root to `PATH` once per shell session so the examples below
run against the current source tree:

```bash
export PATH="$PWD:$PATH"
groundkit list
```

```powershell
$env:Path = "$PWD;$env:Path"
groundkit list
```

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
groundkit add ./my-library

# Use an explicit documentation directory
groundkit add /path/to/repo --path docs

# Set package identity and version
groundkit add ./my-library --name my-library --pkg-version 1.0.0

# Save a shareable copy while installing locally
groundkit add ./my-library --name my-library --pkg-version 1.0.0 --save ./artifacts

# Build a repository at a tag
groundkit add https://github.com/mattpocock/skills --tag v1.0.0
```

Website documentation can be loaded from an automatically discovered or direct
`llms.txt` endpoint:

```bash
# Auto-fetch llms-full.txt or llms.txt
groundkit add https://agentgateway.dev

# Fetch this llms.txt index and follow its linked documents
groundkit add https://agentgateway.dev/llms.txt

# Override the generated package name
groundkit add https://agentgateway.dev --name agent-gateway

# Fall back to readable extraction for an article without llms.txt
groundkit add https://agentgateway.dev/blog/2026-08-20-benchmarking-agentgateway-epp-proxy-overhead/

# Fetch raw Markdown from a GitHub blob URL
groundkit add https://github.com/agentgateway/agentgateway/blob/main/README.md --name agentgateway-readme
```

For website roots, GroundKit probes `/llms-full.txt` and then `/llms.txt`. If
neither endpoint exists, it fetches the URL directly. HTML is reduced to
readable article content; Markdown and other text responses are indexed as-is.

Packages can also be shared as hosted SQLite artifacts. Build and save a
package, place the `.db` file on an HTTP server, then add its URL. The URL may
end in `.db` or use a `name@version` path:

```bash
groundkit add https://github.com/mattpocock/skills --path docs \
	--name mattpocock-skills --pkg-version 1.2.3 \
	--save ./artifacts/mattpocock-skills@1.2.3.db

groundkit add https://packages.example.com/mattpocock-skills@1.2.3
```

To add a saved database from the local filesystem, pass its `.db` path directly:

```bash
groundkit add ./mattpocock-skills@1.2.3.db
```

For Git repository URLs without `--tag`, GroundKit selects the latest stable tag when tags are available. `--choose-tag` changes this to an interactive selection.

| Command                                                                                  | Purpose                                                                                  |
| ---------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------- |
| `add <source> [--path <path>] [--name <name>] [--pkg-version <version>] [--save <path>]` | Build and install a package; local directories auto-detect the documented common folders |
| `catalog [query]`                                                                        | Browse curated sources                                                                   |
| `list`                                                                                   | List installed packages                                                                  |
| `query <package-id> <topic>`                                                             | Search local documentation                                                               |
| `inspect <package-id>`                                                                   | Inspect package and source metadata                                                      |
| `refresh <package-id>`                                                                   | Rebuild from stored source metadata                                                      |
| `import <package-file>`                                                                  | Install a SQLite package                                                                 |
| `export <package-id> <destination>`                                                      | Copy a package artifact                                                                  |
| `search-packages <registry> <name> [version]`                                            | Search hosted registry                                                                   |
| `download-package <registry> <name> <version>`                                           | Download and install package                                                             |
| `install <registry/name\|name\|source> [version]`                                        | Install from the registry, else build from source                                        |
| `remove <name[@version]>`                                                                | Remove one installed package version                                                     |

`list` renders a package inventory table with package name, version, size, documents, sections, and a totals summary.

### Remove packages

Remove one installed package version by name or by an exact `name@version`
selector:

```bash
groundkit remove mattpocock-skills
groundkit remove mattpocock-skills@1.2.3
groundkit remove agentgateway
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
groundkit install react

# Exact published version
groundkit install npm/react 19.1.0

# Multi-version packages need an explicit version
groundkit install npm/angular 20.3.15
```

**Path B: build from source.** Used when the registry has no match or cannot be
reached. A curated name is expanded to its catalog repository and documentation
path and cloned; a local folder, Git repository URL, `llms.txt` site, or raw page
is used as supplied.

```bash
# Fallback for a curated name: clones the catalog repository instead of downloading
groundkit install react

# Explicit source instead of a registry name
groundkit install https://github.com/mattpocock/skills
```

Resolution order:

1. Reuse a matching package already in the local store. No network request.
2. Search the configured registry and import the matching artifact.
3. Registry has no match or is unreachable: build from the catalog entry or the supplied source.

A registry error while downloading a matched package is reported instead of
falling back, because a catalog that answers but cannot serve its artifact is a
broken entry rather than a missing package. `download-package` is the strict
registry-only path with no fallback at all; `add` always builds from source and
is the command that supports `--tag` for a reproducible build.

A fallback build indexes the catalog repository's newest stable tag and only
records the requested version as package metadata. When the local build must
match a version exactly, build it explicitly with `groundkit add --tag`.

`--registry-url <URL>` applies to `install`, `search-packages`, and
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
groundkit add https://github.com/facebook/react
Warning: Only a few sections were indexed. Search for the appropriate documentation repository: <Perplexity URL>

groundkit add https://github.com/reactjs/react.dev
```
