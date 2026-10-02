# Registry API

GroundKit accepts a small compatible HTTP registry API.

## Static catalog option

The same consumer commands can use a generated static catalog on GitHub Pages.
Configure the full JSON URL, not just the website root:

```powershell
$env:GROUNDKIT_REGISTRY_URL = "https://OWNER.github.io/REPOSITORY/registry/index.json"
groundkit search-packages npm react
groundkit install npm/react
```

Replace the example with the URL reported by the Pages deployment. Catalog
schema version `1` lists identities, versions, HTTPS asset URLs, byte sizes,
and SHA-256 hashes. Downloads verify size and hash before local import.
The registry workflow publishes snapshot Release assets; the docs workflow
deploys their catalog alongside this site. Availability requires successful CI
and Pages setup. An API base URL instead uses the routes below.

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

The server may be local, internal, or hosted. Configure its base URL with `GROUNDKIT_REGISTRY_URL`.

## Local server

The registry server is an optional self-hosted Docker deployment. It stores
metadata in SQLite and package files in MinIO or another S3-compatible store.
It is not required for local CLI or MCP queries.

From a cloned repository, create the environment file and replace both example
secrets before starting the services:

```bash
cp .env.example .env
```

Start the registry API and its MinIO dependencies:

```bash
docker compose up --build -d registry-api
```

Check the API:

```bash
curl http://localhost:8080/health
```

The API listens on `http://localhost:8080`. MinIO stores package files on port
`9000` and its administration console is available on port `9001`.

Publish packages from the CLI by configuring the API address and upload token:

```powershell
$env:REGISTRY_SERVER_URL = "http://localhost:8080"
$env:REGISTRY_PUBLISH_KEY = "the-value-from-.env"
groundkit registry publish-all --dir registry --output ./dist-packages
```

Configure consumers with the same API address:

```powershell
$env:GROUNDKIT_REGISTRY_URL = "http://localhost:8080"
groundkit search-packages npm react
groundkit install npm/react
```

For a one-off consumer request, use `--registry-url` instead of setting the
environment variable:

```powershell
groundkit search-packages npm react --registry-url http://localhost:8080
```

The Compose volumes `registry-data` and `minio-data` contain the registry
state and package files. Back up both volumes. For production, expose only the
registry API through HTTPS, keep MinIO private, replace development credentials,
and do not commit `.env` or publish tokens.
