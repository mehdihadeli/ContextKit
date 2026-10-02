# GroundKit CLI

`groundkit` is the single CLI for building, installing, querying, and sharing
documentation packages. It also starts MCP and exposes registry maintainer
commands. Installed packages are SQLite databases; queries run locally.

## Install and run

```bash
dotnet tool install --global GroundKit
groundkit --help
```

During development, run these commands from the repository root:

```powershell
./groundkit.ps1 --help
dotnet run --project src/GroundKit.Cli -- --help
```

Use `./groundkit` in a POSIX shell or `groundkit.cmd` on Windows. Add the
repository root to `PATH` to use the launcher as `groundkit`.

## Root commands

All commands below follow `groundkit`. `<argument>` is required;
`[argument]` is optional. Aliases have the same arguments and behavior.

| Command | Aliases | Purpose |
| --- | --- | --- |
| `add <source>` | `a` | Build documentation from a curated source, Git, a directory, or a URL and install it locally. |
| `install <source-or-package> [version]` | `i` | Reuse a local package, try the registry, then fall back to a local build. Also accepts local and remote `.db` files. |
| `import <package-file>` | `im` | Import a local SQLite package without rebuilding. |
| `export <package-id> <destination>` | `ex`, `exp` | Copy an installed package for sharing. |
| `list` | `l`, `ls` | List installed packages and versions. |
| `inspect <package-id>` | `ins`, `info` | Show package metadata, source information, and local path. |
| `query <package-id> <topic>` | `q` | Search local documentation and return JSON. Use `name@version` for an exact installed version. |
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
| `groundkit mcp` | None | Start MCP over stdio, usually launched by an agent client. |
| `groundkit mcp http` | `groundkit mcp h` | Start MCP over Streamable HTTP. |
| `groundkit mcp --http [port]` | Convenience form | Run the HTTP subcommand with an optional port. |

| Option | Alias | Applies to | Purpose |
| --- | --- | --- | --- |
| `--libs <names>` | `-l` | Stdio and HTTP | Restrict packages to a comma-separated list. |
| `--urls <urls>` | `-u` | HTTP | Set listen URLs. |
| `--host <host>` | None | HTTP | Set the bind host. |
| `--port <port>` | None | HTTP | Set the listen port. |

```bash
groundkit mcp --libs react,vite
groundkit mcp http --host 127.0.0.1 --port 4000
groundkit mcp h -u http://localhost:4000
```

## Registry maintainer commands

These commands operate on YAML definitions and build artifacts, not the local
installed-package list. `GroundKit.Registry` supplies the implementation as a
library; there is no separate CLI installation.

| Command | Aliases after `registry` | Purpose |
| --- | --- | --- |
| `groundkit registry list` | `l`, `ls` | List registry definitions. |
| `groundkit registry validate` | `v`, `val` | Validate definition syntax and identities. |
| `groundkit registry build <name> [version]` | `b` | Build a definition into a `.db` artifact. |
| `groundkit registry build-all` | `ba` | Build every definition and declared version. |
| `groundkit registry publish <name> [version]` | `p`, `pub` | Build and upload a package to a compatible API, skipping an existing version. |
| `groundkit registry publish-all` | `pa` | Build and upload all definitions, continuing after individual failures. |
| `groundkit registry bundle` | `bd`, `bun` | Create a ZIP or tar.gz archive of built packages and metadata. |
| `groundkit registry import-bundle <path>` | `ib` | Extract a bundle into an artifact directory; it does not install packages. |
| `groundkit registry catalog-index` | None | Generate static `index.json` and content-addressed Release assets from built packages. |

| Option | Alias | Applies to | Default |
| --- | --- | --- | --- |
| `--dir <path>` | `-d` | List, validate, build, publish, and catalog-index commands | `registry` |
| `--output <path>` | `-o` | Build, publish, bundle, import-bundle, and catalog-index commands | `./dist-packages` |
| `--format <format>` | `-f` | `bundle` | `zip`; also accepts `tar.gz` and `tgz` |
| `--destination <path>` | `-t` | `bundle`, `catalog-index` | Bundle archive path; catalog directory defaults to `./dist-catalog`. |
| `--base-url <URL>` | None | `catalog-index` | Required HTTPS Release asset directory URL. |

See the [registry tooling README](../GroundKit.Registry/README.md) for YAML,
publishing configuration, and contributor workflows.

## Common workflows

Build and query documentation locally:

```bash
groundkit add react
groundkit inspect react
groundkit query react "useEffect cleanup" --pretty
```

Search a registry, then install an approved version:

```bash
groundkit search-packages npm react
groundkit download-package npm react 19.1.0
groundkit install npm/react 19.1.0
```

The version above must exist on the configured registry. `install` accepts bare
names such as `react` (default manager: `npm`), `npm/react`, local `.db` paths,
and HTTPS package URLs. Direct package files do not require a registry API.

## Registry addresses and hosting

| Setting | Used by | Purpose |
| --- | --- | --- |
| `RegistryUrl` in `.groundkit/config.json` | Consumer commands | API base URL or full static `index.json` URL; defaults to the public GroundKit Pages catalog. |
| `GROUNDKIT_REGISTRY_URL` | Consumer commands | Override the project registry address for the current environment. |
| `--registry-url <URL>` | `search-packages`, `download-package`, `install` | Override the registry for one invocation; highest priority. |
| `REGISTRY_SERVER_URL` | `registry publish` and `publish-all` | Publishing API address; defaults to `http://localhost:8080`. |
| `REGISTRY_PUBLISH_KEY` | Publishing commands | Upload bearer token; supply securely, not in committed configuration. |

```powershell
$env:GROUNDKIT_REGISTRY_URL = "http://localhost:8080"
groundkit search-packages npm react
```

For a one-off self-hosted or Pages catalog request, use the command option:

```powershell
groundkit search-packages npm react --registry-url https://registry.example.com
groundkit install npm/react --registry-url https://OWNER.github.io/REPOSITORY/registry/index.json
```

Precedence is `--registry-url`, `GROUNDKIT_REGISTRY_URL`, project
`RegistryUrl`, then the public Pages catalog.

The registry API is a separate application, `GroundKit.Registry.Server`. The CLI
does not start it and does not reference its project. See the
[server README](../GroundKit.Registry.Server/README.md) for self-hosting.

For Releases + Pages, configure the full catalog URL, not just the Pages site:

```powershell
$env:GROUNDKIT_REGISTRY_URL = "https://OWNER.github.io/REPOSITORY/registry/index.json"
groundkit search-packages npm react
groundkit install npm/react
```

Replace `OWNER` and `REPOSITORY` with the deployed site, or use the catalog URL
reported by the Pages workflow. Static downloads verify byte size and SHA-256
before import. Direct Release `.db` URLs also work, but do not use catalog
checksum verification. API URLs continue to use the self-hosted API protocol.

See the [main guide](../../README.md#cli-reference) for more examples and the
[community registry guide](../../registry/README.md) for definition contributions.
