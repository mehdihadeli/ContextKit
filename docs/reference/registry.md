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

Start the Compose deployment from the repository root:

```bash
cp .env.example .env
docker compose up --build
```

The API listens on `http://localhost:8080` and MinIO listens on `http://localhost:9001`. Set `REGISTRY_PUBLISH_KEY` in `.env` for authenticated package uploads.
