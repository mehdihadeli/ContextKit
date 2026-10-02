# Release versioning

GroundKit uses Nerdbank.GitVersioning (NBGV) with GitHub Flow. `main` is the
long-lived branch. Changes arrive through pull requests, and release tags are
created only from approved commits on `main`.

The release contract is:

- Ordinary merges advance a commit-height preview version.
- RC and stable releases start with a reviewed change to `version.json`.
- CI validates release tags against the version calculated by NBGV.
- Preview and RC packages include a UTC date and GitHub Actions run number.
- Stable packages use the clean `MAJOR.MINOR.PATCH` version.

## Sources of truth

[`version.json`](../../version.json) contains version intent. NBGV calculates
SemVer, assembly metadata, and commit height from that file and the Git history.
The local tool manifest pins the NBGV CLI used by contributors and CI.

Current preview configuration:

```json
{
  "version": "1.0.0-preview.{height}",
  "publicReleaseRefSpec": [
    "^refs/heads/main$",
    "^refs/tags/v\\d+\\.\\d+\\.\\d+(?:-rc\\.\\d+)?$"
  ]
}
```

Use the same calculation locally and in CI:

```bash
dotnet tool restore
dotnet nbgv get-version -v SemVer2
```

An untagged commit can include `.g<commit>` in the calculated version. That
suffix identifies the source commit and is not part of the public release tag.

## Version shapes

The workflow uses these effective artifact versions:

| Source ref | Effective version | Environment | Publication |
| --- | --- | --- | --- |
| `main`, preview height `0` | `1.0.0-preview.0` | none | Validation only |
| `main`, preview height `1+` | `1.0.0-preview.N.YYDDD.RUN_NUMBER` | dev | NuGet package and archives |
| `v1.0.0-rc.1` | `1.0.0-rc.1.YYDDD.RUN_NUMBER` | staging | NuGet package and archives |
| `v1.0.0` | `1.0.0` | production | NuGet package and archives |

`YYDDD` is the UTC two-digit year and day of year. `RUN_NUMBER` is the
monotonically increasing GitHub Actions workflow run number. The date and run
number make separately built preview or RC artifacts distinguishable; they do
not replace the NBGV preview or RC ordinal.

GroundKit currently publishes the `GroundKit` CLI package to NuGet.org and
three self-contained archives (`linux-x64`, `osx-x64`, and `osx-arm64`). The
workflow does not currently publish a Docker image or deploy to an environment;
the environment names describe release intent and CI permissions.

## Preview releases

Do not edit `preview.1` to create `preview.2`. NBGV derives the ordinal from
Git commit height. Ordinary feature, fix, test, and documentation pull
requests should not change `version.json`.

The initial preview-train commit is validation-only when it calculates
`preview.0`. The first ordinary merge after it publishes `preview.1`, then
later accepted changes publish the next preview number.

Start a new preview train with a reviewed version-intent change:

```bash
./scripts/release-version.sh prepare-train 1.1.0
git add version.json
git commit -m "chore: start 1.1.0 preview train"
git push
```

The helper changes the version to `1.1.0-preview.{height}` and removes any
bootstrap height-offset properties if they are present. It does not commit,
push, or publish.

## RC and stable releases

RC and stable version changes are prepared on short-lived branches and merged
through the protected `main` branch. Tag the merged commit only after CI
validation succeeds.

Prepare the first RC train:

```bash
./scripts/release-version.sh prepare-rc 1.0.0
git add version.json
git commit -m "chore: prepare 1.0.0 RC train"
git push -u origin chore/prepare-1.0.0-rc
```

After the pull request is merged and local `main` is updated, create and push
the tag:

```bash
./scripts/release-version.sh tag
git push origin v1.0.0-rc.1
```

Later RC fixes do not edit `version.json`; another merge advances the
`1.0.0-rc.{height}` template to the next RC ordinal.

Prepare stable in the same way:

```bash
./scripts/release-version.sh prepare-stable 1.0.0
git add version.json
git commit -m "chore: prepare 1.0.0"
git push -u origin chore/prepare-1.0.0
```

After that change is merged and validated:

```bash
./scripts/release-version.sh tag
git push origin v1.0.0
```

`prepare-stable` removes height-offset properties. The stable tag must point to
the merged stable-preparation commit, and CI publishes clean `1.0.0` metadata.

## CI behavior

[`.github/workflows/build-and-publish.yml`](../../.github/workflows/build-and-publish.yml)
runs build, tests, and the Docker MCP smoke tests for pull requests and pushes.
Version calculation and publication run only for `main` and `v*` tags.

Every version-calculating job checks out full history and tags:

```yaml
- uses: actions/checkout@v5
  with:
    fetch-depth: 0
```

The workflow uses these stages:

1. `build-test` restores, builds, tests, and runs the Docker MCP smoke tests.
2. `calculate-version` runs NBGV and selects the effective version and target
   environment.
3. `publish` packs and pushes the CLI, creates self-contained archives, and
   updates or publishes the matching Release Drafter release.

For a syntactically valid release tag, CI removes the `v` prefix and compares
the tag with NBGV’s calculated version after removing only `.g<commit>`. A
mismatch fails the workflow, so a tag such as `v1.0.0-rc.3` cannot publish a
commit that calculates as `1.0.0-rc.2`.

The publish job uses `if: always()` for Release Drafter so release notes remain
up to date when an earlier publication step fails. The job still fails and the
artifact or package must be fixed and rerun.

## Release Drafter

[`.github/release-drafter.yml`](../../.github/release-drafter.yml) classifies
pull requests by labels and excludes `skip-changelog`. `include-pre-releases:
false` keeps stable release notes based on the previous stable release.

On `main`, Release Drafter updates a rolling preview draft. On RC and stable
tags, the workflow passes the calculated version and publishes the matching
release. Release Drafter does not create the Git tag; `dotnet nbgv tag` creates
it locally, and the operator pushes it explicitly.

## Quick checks

Before pushing a release tag, run:

```bash
dotnet tool restore
dotnet nbgv get-version -v SemVer2
dotnet test GroundKit.slnx --configuration Release
```

Then inspect `version.json`, confirm the tag was created from the merged
`main` commit, and push only that tag. The release helper never commits or
pushes on your behalf.
