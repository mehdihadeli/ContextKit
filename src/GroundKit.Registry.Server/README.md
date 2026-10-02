# GroundKit Registry Server

`GroundKit.Registry.Server` is the optional HTTP registry host. It provides
package discovery, metadata, downloads, and authenticated uploads. SQLite stores
registry metadata; MinIO or compatible S3 storage holds the package files.

## Difference from registry tooling

| Capability | `GroundKit.Registry` | `GroundKit.Registry.Server` |
| --- | --- | --- |
| Project type | Command library loaded by `GroundKit.Cli`. | Independent ASP.NET Core application. |
| Entry point | `groundkit registry <command>` | `dotnet run --project src/GroundKit.Registry.Server` or Docker Compose. |
| Reads YAML definitions | Yes. | No. |
| Builds and bundles documentation | Yes. | No; accepts already-built package files. |
| Publishes packages | Sends HTTP uploads with `publish` and `publish-all`. | Receives uploads and stores metadata and artifacts. |
| Hosts search and download endpoints | No. | Yes. |
| Needs S3-compatible storage | No. | Yes. |
| Needed for local documentation queries | No; consumer CLI and MCP query installed packages. | No; it is used during discovery and transfer. |

The [tooling README](../GroundKit.Registry/README.md) covers package creation;
the [CLI README](../GroundKit.Cli/README.md) covers installing and querying them.
Neither project references this host. Start it independently, without a
`groundkit registry serve` command.

## Run with Docker Compose

From the repository root, create a local environment file if one does not exist:

```bash
cp .env.example .env
```

Set `REGISTRY_PUBLISH_KEY` and storage credentials in that file before starting:

```bash
docker compose up --build registry-api
```

Compose starts MinIO and initializes its bucket before the API. The root
Dockerfile publishes this server application, not the registry command library.

| Service | Local address | Use |
| --- | --- | --- |
| Registry API | `http://localhost:8080` | Address for GroundKit consumers and publishers. |
| MinIO S3 endpoint | `http://localhost:9000` | Package object storage. |
| MinIO console | `http://localhost:9001` | Storage administration, not the GroundKit API. |

The `registry-data` volume stores SQLite metadata; `minio-data` stores package
files. Back up both when preserving a registry deployment.

## Run from source

Prepare the same Compose environment file, then start storage:

```bash
docker compose up -d minio minio-init
```

Compose reads `.env`; `dotnet run` does not load it automatically. Set matching
storage credentials and `REGISTRY_PUBLISH_KEY` in the host's environment. For a
local metadata path on Windows, run from the repository root:

```powershell
$env:REGISTRY_DATABASE_PATH = Join-Path $PWD ".groundkit/registry-server/registry.db"
$env:MINIO_ENDPOINT = "http://localhost:9000"
dotnet run --project src/GroundKit.Registry.Server -- --urls http://localhost:8080
```

To run a published host, configure the same environment variables and use:

```bash
dotnet publish src/GroundKit.Registry.Server -c Release -o ./artifacts/registry-server
dotnet ./artifacts/registry-server/GroundKit.Registry.Server.dll --urls http://localhost:8080
```

## Configuration

| Environment variable | Default | Purpose |
| --- | --- | --- |
| `ASPNETCORE_URLS` | Host default; Compose sets `http://+:8080` | Listen addresses; `--urls` also sets them. |
| `REGISTRY_DATABASE_PATH` | `/data/registry.db` | SQLite metadata file; set a writable path when running outside Docker. |
| `REGISTRY_PUBLISH_KEY` | None | Bearer token for uploads; absent key means uploads are denied. |
| `MINIO_ENDPOINT` | `http://localhost:9000` | S3-compatible endpoint; Compose uses `http://minio:9000`. |
| `MINIO_BUCKET` | `groundkit-packages` | Package object bucket. |
| `MINIO_ACCESS_KEY` | `minioadmin` | Development storage access key. |
| `MINIO_SECRET_KEY` | `minioadmin` | Development storage secret key. |

The host attempts to create the bucket at startup; storage must be reachable
with credentials that permit that operation. Client address variables do not
configure the host: `REGISTRY_SERVER_URL` configures publishers and
`GROUNDKIT_REGISTRY_URL` configures consumers.

## API endpoints

| Method | Route | Behavior | Authentication |
| --- | --- | --- | --- |
| `GET` | `/health` | Return host health status. | None. |
| `GET` | `/search?registry=npm&name=react&version=latest` | Return matching packages; filters are optional. | None. |
| `GET` | `/packages/{registry}/{name}/{version}` | Return package metadata or 404. | None. |
| `GET` | `/packages/{registry}/{name}/{version}/download` | Stream the SQLite package or return 404. | None. |
| `POST` | `/packages/{registry}/{name}/{version}` | Upload package bytes and register metadata. | `Authorization: Bearer <publish-key>`. |

Downloads use `application/octet-stream`. Metadata includes identity, byte size,
and SHA-256. The upload key authorizes writes only; read endpoints are public.

## Build, publish, and consume

Start the server first, then run these commands in a separate terminal. Supply
the same publish key to both host and publisher without committing it:

```powershell
$env:REGISTRY_SERVER_URL = "http://localhost:8080"
# Supply REGISTRY_PUBLISH_KEY securely to this terminal.
groundkit registry validate --dir registry
groundkit registry publish react --dir registry --output ./dist-packages

$env:GROUNDKIT_REGISTRY_URL = "http://localhost:8080"
groundkit search-packages npm react
groundkit download-package npm react latest
groundkit query react "useEffect cleanup"
```

`publish` builds the artifact when it is missing from the target API. The
committed React definition uses `latest`; use the declared version for a
versioned definition. Consumers can use `install npm/react` when local reuse
and build fallback are preferred over an exact registry download.

After installation, CLI queries and MCP read the local package. They do not need
this registry server to stay online.

## Deployment boundaries

Use HTTPS and replace development storage credentials for non-local hosting.
To keep packages private, restrict read access with a network boundary or an
authenticated reverse proxy. A publish key alone does not protect downloads.
This host does not provide tenant isolation or a complete production security
policy.

GitHub Pages cannot run this application. Releases + Pages distribution does not
require this host: the maintainer library builds packages, Releases stores assets,
and the docs workflow deploys static catalog metadata. Consumers configure the
full `registry/index.json` URL for that mode. Use this host when API-based
publishing and discovery are preferred; client API support remains unchanged.
