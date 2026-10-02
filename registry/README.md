# Community Registry

Registry definitions describe where documentation comes from and how to build a GroundKit package. Each YAML file defines an unversioned source or one or more versioned sources.

GroundKit consumes packages through a compatible HTTP API, a static catalog, or
direct package URLs. The [Registry Update workflow](../.github/workflows/registry-update.yml)
validates definitions, builds packages, and publishes snapshot Releases on `main`.
The docs workflow deploys the newest catalog to `registry/index.json` alongside
the Pages site. Actions artifacts remain available for temporary transfers.
Public availability requires successful CI and Pages configuration. Optional
self-hosted API publishing is also supported by the maintainer commands.

## Layout

Definitions live under a package-manager directory. File names identify package
names, and nested paths are supported for Go module paths:

```text
registry/npm/react.yaml -> npm/react
registry/pip/fastapi.yaml -> pip/fastapi
registry/maven/spring-boot.yaml -> maven/spring-boot
registry/go/github.com/spf13/cobra.yaml -> go/github.com/spf13/cobra
```

The `name` field must match the filename or its path relative to the manager
directory. The manager directory becomes the registry name.

## Definition format

### What is a package?

A registry package is a versioned, searchable documentation bundle for one
library or module. The registry definition tells GroundKit where to get the
documentation and which files to ingest. The registry builder turns that
source into a SQLite package containing document text, chunks, metadata, and
search indexes. MCP clients download these packages instead of cloning source
repositories themselves.

The package manager directory is metadata used as the registry name. It does
not change how documentation is built:

| Directory | Registry name | Package examples |
| --- | --- | --- |
| `registry/npm/` | `npm` | Next.js, React, Tailwind CSS |
| `registry/pip/` | `pip` | FastAPI, Flask, Django, Pydantic |
| `registry/maven/` | `maven` | Spring Boot, JUnit, Micrometer |
| `registry/go/` | `go` | `github.com/spf13/cobra` |

### npm: Git documentation

```yaml
name: react
description: "JavaScript UI library"
repository: https://github.com/reactjs/react.dev

source:
  type: git
  url: https://github.com/reactjs/react.dev
  docs_path: src/content
```

### pip: ZIP documentation

```yaml
name: python
description: "Python programming language documentation"

versions:
  - versions: ["3.14", "3.13", "3.12"]
    source:
      type: zip
      url: "https://docs.python.org/3/archives/python-{version}-docs-html.zip"
      docs_path: "python-{version}-docs-html"
      exclude_paths:
        - "whatsnew/**"
        - "changelog.html"
```

### Maven: versioned Git documentation

```yaml
name: spring-boot
description: "Spring Boot framework documentation"
repository: https://github.com/spring-projects/spring-boot

versions:
  - version: "3.4.0"
    tag: v3.4.0
    source:
      type: git
      url: https://github.com/spring-projects/spring-boot
      docs_path: spring-boot-project/spring-boot-docs/src/docs/asciidoc
  - version: "3.3.0"
    tag: v3.3.0
    source:
      type: git
      url: https://github.com/spring-projects/spring-boot
      docs_path: spring-boot-project/spring-boot-docs/src/docs/asciidoc
```

### Go: full module path

For Go modules, place the YAML under the module path and use that full path in
`name`:

```yaml
# registry/go/github.com/spf13/cobra.yaml
name: github.com/spf13/cobra
description: "Go command-line application framework"
repository: https://github.com/spf13/cobra

source:
  type: git
  url: https://github.com/spf13/cobra
  docs_path: .
```

### Unversioned definition

```yaml
name: react
description: "User interface library"
repository: https://github.com/reactjs/react.dev

source:
  type: git
  url: https://github.com/reactjs/react.dev
  docs_path: src/content
```

Supported source types are `git`, `zip`, local directory, `llms.txt`, and raw
page. ZIP sources are downloaded, extracted, and can filter files with
`exclude_paths`, as shown in the pip example above.

Documentation inputs should be Markdown, MDX, HTML, AsciiDoc, or
reStructuredText where supported by the ingestion pipeline.

## Versioned definitions

Use `versions` instead of `source` when a package must be built for exact releases. Do not combine both shapes:

```yaml
name: example
description: "Example library"
repository: https://github.com/example/example

versions:
  - min_version: "1.0.0"
    tag_pattern: "v{version}"
    source:
      type: git
      url: https://github.com/example/example
      docs_path: docs
```

Automatic discovery of all upstream releases is not implemented. List exact versions when reproducibility is required. The maintainer CLI can build them and publish to a configured GroundKit API.

## Add a definition

Contributions are welcome through pull requests. Contributors do not need
registry hosting or publishing credentials.

1. Add `registry/<manager>/<name>.yaml` and keep its `name` consistent with the filename or nested path.
2. Point `docs_path` at documentation and check that its license permits redistribution.
3. Validate and build the definition locally, then import and inspect the result.
4. Open a pull request including the source URL, selected version or ref, and build results. Maintainers review before publishing.

```bash
groundkit registry validate --dir registry
groundkit registry build react --dir registry --output ./dist-packages
groundkit import ./dist-packages/react@latest.db
groundkit inspect react
```

Replace `react` with your package name and `latest` with the built version.
PR validation should be read-only and have no publishing secrets. Release uploads
and Pages deployment should run only from trusted, merged definitions. Adding a
definition does not automatically install its package for consumers.

A registry builder should reject malformed definitions, missing documentation roots, and definitions that produce empty or suspiciously small packages. CI currently validates required fields and builds every definition; package health thresholds remain future work.

## Included definitions

The initial definitions mirror the curated starter catalog. They are intentionally simple Git sources so each entry can be reviewed and extended independently.
