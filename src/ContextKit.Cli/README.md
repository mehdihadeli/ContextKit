# ContextKit CLI

`ck` is the single CLI for building, installing, querying, and sharing
documentation packages. It also starts MCP and exposes registry maintainer
commands. Installed packages are SQLite databases; queries run locally.

## Install and run

```bash
dotnet tool install --global ContextKit
# ToolCommandName registers the shim as `ck`
ck --help
```

The repository root ships launchers for the same command under both names, so a
clone needs no install:

```powershell
./ck.ps1 --help            # PowerShell
./groundkit.ps1 --help     # PowerShell, long name
```

```bash
./ck --help                # POSIX shell
```

Add the repository root to `PATH` and every example below runs against the
current source tree as plain `ck`:

```bash
export PATH="$PWD:$PATH"
ck list
```

```powershell
$env:Path = "$PWD;$env:Path"
ck list
```

### Command name

`ck` is the tool command and the primary name in this documentation. The
repository root also ships `groundkit`, `groundkit.cmd`, and `groundkit.ps1`
launchers that accept exactly the same arguments, so long-form invocations keep
working during development:

```bash
ck query angular@19 "component lifecycle"
groundkit query angular@19 "component lifecycle"
```

A .NET tool installs a single shim named by `ToolCommandName`, so the long name
after a global install is a shell concern:

```powershell
Set-Alias groundkit ck
```

```bash
alias groundkit=ck
```

## Root commands

All commands below follow `ck`. `<argument>` is required;
`[argument]` is optional. Aliases have the same arguments and behavior.

| Command | Aliases | Purpose |
| --- | --- | --- |
| `add <source>` | `a` | Build documentation from a curated source, Git, a directory, or a URL and install it locally. |
| `install <source-or-package> [version]` | `i` | Reuse a local package, try the registry, then fall back to a local build. Also accepts local and remote `.db` files. |
| `import <package-file>` | `im` | Import a local SQLite package without rebuilding. |
| `export <package-id> <destination>` | `ex`, `exp` | Copy an installed package for sharing. |
| `list` | `l`, `ls` | List installed packages and versions. |
| `inspect <package-id>` | `ins`, `info` | Show package metadata, source information, and local path. |
| `query <package-id> <topic>` | `q` | Search local documentation and return JSON. Use `name@version` for an exact installed version. Pass a single quoted question to let the package be detected from it. |
| `refresh <package-id>` | `rf`, `ref` | Rebuild an installed package from its stored source. |
| `remove <name[@version]>` | `rm`, `del` | Remove one installed version; a bare name with multiple versions requires selection. |
| `catalog [query]` | `c`, `cat` | Browse curated documentation sources, not published registry versions. |
| `search-packages <registry> <name> [version]` | `sp`, `search` | Search registry metadata without installing anything. |
| `download-package <registry> <name> <version>` | `dp`, `dl`, `download` | Download and install one exact registry package, without local fallback. |

### Root command options

| Option | Alias | Applies to | Purpose |
| --- | --- | --- | --- |
| `--path <path>` | `-p` | `add` | Select documentation within the source. |
| `--name <name>` | `-n` | `add` | Override package identity and display name. |
| `--pkg-version <version>` | `-v` | `add` | Set package and source version metadata. |
| `--save <path>` | `-s` | `add` | Save another copy to a `.db` file or directory. |
| `--tag <ref>` | `-t` | `add` | Select a Git tag or branch. |
| `--choose-tag` | `-c` | `add` | Choose a stable Git tag interactively. |
| `--pretty` | None | `query` | Format JSON output for reading. |
| `--verbose` | None | Any command | Enable information-level logs. |
| `--help` | `-h` | Any command or branch | Show available arguments and options. |

## MCP commands

MCP serves installed documentation to agents. It does not automatically install
packages when it starts. HTTP transport exposes `/mcp`.

| Command | Alias or alternate form | Purpose |
| --- | --- | --- |
| `ck mcp` | None | Start MCP over stdio, usually launched by an agent client. |
| `ck mcp http` | `ck mcp h` | Start MCP over Streamable HTTP. |
| `ck mcp --http [port]` | Convenience form | Run the HTTP subcommand with an optional port. |

| Option | Alias | Applies to | Purpose |
| --- | --- | --- | --- |
| `--libs <names>` | `-l` | Stdio and HTTP | Restrict packages to a comma-separated list. |
| `--urls <urls>` | `-u` | HTTP | Set listen URLs. |
| `--host <host>` | None | HTTP | Set the bind host. |
| `--port <port>` | None | HTTP | Set the listen port. |

```bash
ck mcp --libs react,vite
ck mcp http --host 127.0.0.1 --port 4000
ck mcp h -u http://localhost:4000
```

## Registry maintainer commands

These commands operate on YAML definitions and build artifacts, not the local
installed-package list. `ContextKit.Registry` supplies the implementation as a
library; there is no separate CLI installation.

| Command | Aliases after `registry` | Purpose |
| --- | --- | --- |
| `ck registry list` | `l`, `ls` | List registry definitions. |
| `ck registry validate` | `v`, `val` | Validate definition syntax and identities. |
| `ck registry build <name> [version]` | `b` | Build a definition into a `.db` artifact. |
| `ck registry build-all` | `ba` | Build every definition and declared version. |
| `ck registry push-oci` | `po` | Push built packages to an OCI registry and write the digest-pinned reference map. |
| `ck registry bundle` | `bd`, `bun` | Create a ZIP or tar.gz archive of built packages and metadata. |
| `ck registry import-bundle <path>` | `ib` | Extract a bundle into an artifact directory; it does not install packages. |
| `ck registry catalog-index` | None | Generate static `index.json` and content-addressed Release assets from built packages. |

| Option | Alias | Applies to | Default |
| --- | --- | --- | --- |
| `--dir <path>` | `-d` | List, validate, build, push-oci, and catalog-index commands | `registry` |
| `--output <path>` | `-o` | Build, push-oci, bundle, import-bundle, and catalog-index commands | `./dist-packages` |
| `--format <format>` | `-f` | `bundle` | `zip`; also accepts `tar.gz` and `tgz` |
| `--destination <path>` | `-t` | `bundle`, `catalog-index` | Bundle archive path; catalog directory defaults to `./dist-catalog`. |
| `--oci-repository <host/path>` | None | `push-oci`, `catalog-index` | Target OCI repository, for example `ghcr.io/owner/groundkit`. |
| `--oci-references <path>` | None | `push-oci`, `catalog-index` | Digest-pinned reference map; defaults to `oci-references.json`. |
| `--base-url <URL>` | None | `catalog-index` | Required HTTPS asset directory URL when no OCI target is supplied. |

See the [registry tooling README](../ContextKit.Registry/README.md) for YAML,
publishing configuration, and contributor workflows.

## Common workflows

Build and query documentation locally:

```bash
ck add react
ck inspect react
ck query react "useEffect cleanup" --pretty
```

`query` downloads a published package on demand, so the build step is optional
for a library ContextKit publishes in its registry:

```bash
# Downloads npm/vue@latest from the registry, then queries it
ck query vue "composition api"

# Keep it offline: report the missing package and the command that adds it
ck query vue "composition api" --no-install
```

Search a registry, then install an approved version:

```bash
ck search-packages npm react
ck download-package npm react 19.1.0
ck install npm/react 19.1.0
```

The version above must exist on the configured registry. `install` accepts bare
names such as `react` (default manager: `npm`), `npm/react`, local `.db` paths,
and HTTPS package URLs. Direct package files do not require a registry API.

## Registry addresses and hosting

| Setting | Used by | Purpose |
| --- | --- | --- |
| `RegistryUrl` in `.groundkit/config.json` | Consumer commands | API base URL or full static `index.json` URL; defaults to the public ContextKit Pages catalog. |
| `GROUNDKIT_REGISTRY_URL` | Consumer commands | Override the project registry address for the current environment. |
| `--registry-url <URL>` | `search-packages`, `download-package`, `install` | Override the registry for one invocation; highest priority. |
| `GROUNDKIT_OCI_USERNAME` | `registry push-oci` | OCI registry username; falls back to `GITHUB_ACTOR`. |
| `GROUNDKIT_OCI_TOKEN` | `registry push-oci` | OCI registry token or password; falls back to `GITHUB_TOKEN`, then the Docker credential file for the target host; never commit it. |

```powershell
$env:GROUNDKIT_REGISTRY_URL = "http://localhost:8080"
ck search-packages npm react
```

For a one-off catalog request, use the command option:

```powershell
ck search-packages npm react --registry-url https://registry.example.com
ck install npm/react --registry-url https://OWNER.github.io/REPOSITORY/registry/index.json
```

Precedence is `--registry-url`, `GROUNDKIT_REGISTRY_URL`, project
`RegistryUrl`, then the public Pages catalog.

Packages are published as OCI artifacts. The CLI never runs a registry server;
it only reads a catalog and pulls digests. See the
[registry tooling README](../ContextKit.Registry/README.md) for the publishing setup.

For Releases + Pages, configure the full catalog URL, not just the Pages site:

```powershell
$env:GROUNDKIT_REGISTRY_URL = "https://OWNER.github.io/REPOSITORY/registry/index.json"
ck search-packages npm react
ck install npm/react
```

Replace `OWNER` and `REPOSITORY` with the deployed site, or use the catalog URL
reported by the Pages workflow. Static downloads verify byte size and SHA-256
before import. Direct `.db` URLs also work, but do not use catalog checksum
verification.

See the [main guide](../../README.md#cli-reference) for more examples and the
[community registry guide](../../registry/README.md) for definition contributions.
