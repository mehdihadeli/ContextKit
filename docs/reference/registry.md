# Registry

The ContextKit registry is a catalog of prebuilt documentation packages plus the
tooling that publishes them. Consumers only ever read the catalog; nobody has to
run a server to use one.

Packages are described by declarative YAML definitions contributed to this
repository, one file per library, under `registry/<manager>/`. CI turns each
definition into a SQLite package, publishes it to an OCI registry, and records
the result in the catalog. Because the catalog pins a content digest, every
consumer that installs a package downloads the same verified bytes.

## Client shapes

ContextKit's registry client reads two catalog shapes, and the CLI ships no
registry server of its own:

- **Static catalog** — the default, and what the public registry uses: one
  `index.json` mapping every package to an OCI manifest URL, byte size, and
  SHA-256.
- **Compatible HTTP API** — the read routes documented below, used when
  `RegistryUrl` points at an API base URL instead of a `.../index.json` file.

Either way the client resolves one package, downloads it, verifies size and
SHA-256, and imports it into the local store.

## Which packages are catalogued

The starter catalog covers 19 JavaScript and web libraries:

| Category | Libraries |
| --- | --- |
| Frameworks | Next.js, NestJS, Vue, Angular |
| React ecosystem | React, Vite, Vitest |
| Backend and APIs | Express, Fastify, GraphQL, Zod |
| Styling | Tailwind CSS |
| Tooling | TypeScript, ESLint, Prettier |
| Data and testing | Axios, Jest, Playwright, VitePress |

Browse the same list on the [library catalog](/libraries) page. A library that
is missing is one YAML file away — see
[contributing a definition](#contributing-a-definition).

## Contributing a definition

Definitions are declarative, so adding a library does not mean patching the
builder. One file describes where the docs live and which ref to build:

```yaml
# registry/npm/react.yaml
name: react
description: "User interface library"
repository: https://github.com/reactjs/react.dev
source:
  type: git
  url: https://github.com/reactjs/react.dev
  docs_path: src/content
```

A definition with several versions declares each one explicitly, because there
is no automatic upstream version discovery:

```yaml
# registry/npm/angular.yaml
name: angular
description: "Web application framework"
repository: https://github.com/angular/angular
versions:
  - version: "21.0.3"
    tag: "21.0.3"
    source:
      type: git
      url: https://github.com/angular/angular
      docs_path: adev/src/content
```

Validate and build it locally before opening a pull request:

```bash
ck registry validate --dir registry
ck registry build react --dir registry --output ./dist-packages
```

Once merged to the default branch, the
[Registry Update workflow](https://github.com/mehdihadeli/groundkit/blob/main/.github/workflows/registry-update.yml)
builds the definition, pushes the result to GHCR as a content-addressed OCI
artifact, and records the manifest digest in the catalog. From then on
`ck install npm/react` serves it. Publishing runs only from merged definitions,
never from contributor pull requests, so pull requests need no credentials.

See [registry/README.md](https://github.com/mehdihadeli/groundkit/blob/main/registry/README.md)
for the full definition format, including ZIP sources and multi-version
definitions.

## Static catalog

The catalog is a static `index.json` that maps each package to an OCI manifest
digest, its byte size, and its SHA-256. There is no server to run and no
credential needed to read it. Catalog schema version `1` lists identities,
versions, HTTPS manifest or asset URLs, byte sizes, SHA-256 hashes, and an
optional `ociReference`; downloads verify size and hash before local import.

Configure the full JSON URL, not just the website root:

```powershell
$env:GROUNDKIT_REGISTRY_URL = "https://OWNER.github.io/REPOSITORY/registry/index.json"
ck search-packages npm react
ck install npm/react
```

Replace the example with the URL reported by the Pages deployment. For a one-off
consumer request, use `--registry-url` instead of setting the environment
variable:

```powershell
ck search-packages npm react --registry-url https://registry.example.com/registry/index.json
```

Packages for the default catalog are published as content-addressed OCI
artifacts in the GitHub Container Registry, so `install` resolves the artifact
by manifest digest, downloads its single layer, and verifies SHA-256 against the
catalog. No `.db` files are attached to GitHub Releases. Availability requires
successful CI and Pages setup, plus a one-time public visibility flip per GHCR
package. An API base URL instead uses the routes below.

## OCI artifacts

Publishing to an OCI registry uses the OCI Distribution Specification directly:

```text
GET  /v2/<repository>/manifests/<tag|digest>
GET  /v2/<repository>/blobs/<digest>
POST /v2/<repository>/blobs/uploads/
PUT  /v2/<repository>/blobs/uploads/<id>?digest=<digest>
PUT  /v2/<repository>/manifests/<tag>
```

Each package is a single-layer artifact whose layer media type is
`application/vnd.groundkit.package.v1.db` and whose layer is the SQLite package
file. Anonymous pulls work for public packages; authenticated pushes read
credentials from `GROUNDKIT_OCI_USERNAME` and `GROUNDKIT_OCI_TOKEN`, falling back
to `GITHUB_ACTOR` and `GITHUB_TOKEN`, and finally to the host's entry in the
Docker credential file (`$DOCKER_CONFIG/config.json`, default
`~/.docker/config.json`) — the file `docker/login-action` writes, so a workflow
logs in with the standard action instead of exporting a token. Credentials are
matched by registry host, so a file holding several registries never sends one
host's token to another.

Publishing needs write credentials for the OCI registry:

```powershell
$env:GROUNDKIT_OCI_USERNAME = "<github-user>"
$env:GROUNDKIT_OCI_TOKEN = "<token-with-write-packages>"
ck registry push-oci --dir registry --output ./dist-packages `
  --oci-repository ghcr.io/OWNER/REPOSITORY --oci-references ./oci-references.json
```

Consumers verify the manifest digest, blob size, and SHA-256 before import.

The release workflow builds every declared version before it pushes, and one
unbuildable definition would otherwise abort the run.
`ck registry build-all --allow-failures` keeps the artifacts that did build and
exits successfully when at least one definition succeeded. With nothing built it
still fails, so a completely broken registry is never published silently.

## HTTP API routes

A catalog served by an API base URL instead of a static file responds to these
read routes.

### Search

```http
GET /search?registry=npm&name=react&version=19.1.0
```

Returns package entries containing `name`, `registry`, `version`, `description`, and optional `size`.

### Download

```http
GET /packages/npm/react/19.1.0/download
```

Returns the raw SQLite package as `application/octet-stream`.

### Package metadata

```http
GET /packages/npm/react/19.1.0
```

Returns package identity and optional source commit information.

Consumers only need read access to the catalog. Configure its URL with `GROUNDKIT_REGISTRY_URL`.

## Install paths

`install` tries the registry first and builds from source when the registry has
no match or cannot be reached.

| Path | Trigger | What happens |
| --- | --- | --- |
| Registry | A catalog entry matches the requested registry and name | Resolve the manifest digest, download its single layer, verify size and SHA-256, import |
| Source build | No catalog match, or the registry is unreachable | Clone the catalog repository and docs path (curated name), or the supplied source, then index and store locally |

A registry that answers but cannot serve a matched artifact is reported as an
error instead of falling back, because a broken catalog entry is not the same as
a missing package. `download-package` never falls back; `add` always builds.

A source build indexes the catalog repository's newest stable tag and records
the requested version as metadata only, so use `add --tag` when a package must
match a version exactly.

Packages published as several versions have no `latest` tag, so pass an explicit
version — for example `ck install npm/angular 20.3.15`. A catalog entry
pins the manifest digest, which is why a multi-version package installs without
one.
