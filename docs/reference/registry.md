# Registry API

GroundKit's registry client supports two shapes, and the CLI ships no registry
server of its own:

- **Static catalog** - the default, and what the public registry uses: one
  `index.json` mapping every package to an OCI manifest URL, byte size, and
  SHA-256.
- **Compatible HTTP API** - the read routes documented below, used when
  `RegistryUrl` points at an API base URL instead of a `.../index.json` file.

Either way the client resolves one package, downloads it, verifies size and
SHA-256, and imports it into the local store. Publishing is a maintainer
operation that goes to an OCI registry such as GHCR.

## Static catalog option

The same consumer commands can use a generated static catalog on GitHub Pages.
Configure the full JSON URL, not just the website root:

```powershell
$env:GROUNDKIT_REGISTRY_URL = "https://OWNER.github.io/REPOSITORY/registry/index.json"
groundkit search-packages npm react
groundkit install npm/react
```

Replace the example with the URL reported by the Pages deployment. Catalog
schema version `1` lists identities, versions, HTTPS manifest or asset URLs,
byte sizes, SHA-256 hashes, and an optional `ociReference`. Downloads verify
size and hash before local import.

Packages for the default catalog are published as content-addressed OCI
artifacts in the GitHub Container Registry, so `install` resolves the artifact
by manifest digest, downloads its single layer, and verifies SHA-256 against the
catalog. The registry workflow also generates the catalog, ships it as a workflow
artifact, and the docs workflow deploys it alongside this site. No `.db` files
are attached to GitHub Releases. Availability requires successful CI and Pages
setup, plus a one-time public visibility flip per GHCR package. An API base URL
instead uses the routes below.

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

## Search

```http
GET /search?registry=npm&name=react&version=19.1.0
```

Returns package entries containing `name`, `registry`, `version`, `description`, and optional `size`.

## Download

```http
GET /packages/npm/react/19.1.0/download
```

Returns the raw SQLite package as `application/octet-stream`.

## Package metadata

```http
GET /packages/npm/react/19.1.0
```

Returns package identity and optional source commit information.

Consumers only need read access to the catalog. Configure its URL with `GROUNDKIT_REGISTRY_URL`.

## Static catalog

Packages live in an OCI registry. The catalog is a static `index.json` that maps
each package to an OCI manifest digest, its byte size, and its SHA-256. There is
no server to run and no credential needed to read.

```powershell
$env:GROUNDKIT_REGISTRY_URL = "https://OWNER.github.io/REPOSITORY/registry/index.json"
groundkit search-packages npm react
groundkit install npm/react
```

For a one-off consumer request, use `--registry-url` instead of setting the
environment variable:

```powershell
groundkit search-packages npm react --registry-url https://registry.example.com/registry/index.json
```

Publishing is a maintainer operation and needs write credentials for the OCI
registry:

```powershell
$env:GROUNDKIT_OCI_USERNAME = "<github-user>"
$env:GROUNDKIT_OCI_TOKEN = "<token-with-write-packages>"
groundkit registry push-oci --dir registry --output ./dist-packages `
  --oci-repository ghcr.io/OWNER/REPOSITORY --oci-references ./oci-references.json
```

Consumers verify the manifest digest, blob size, and SHA-256 before import.

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
version - for example `groundkit install npm/angular 20.3.15`. A catalog entry
pins the manifest digest, which is why a multi-version package installs without
one.
