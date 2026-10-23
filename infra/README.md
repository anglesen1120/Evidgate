# infra/

Compose files, Dockerfiles, OTel collector config, dashboards and alert rules (00 §5.2). Production topology is owned by plan 10; the dev stack of 01.8.1 lives here.

## Local dev stack (01.8.1-T2)

| Path                              | What it is                                                                                                                                                                                                            |
| --------------------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `compose.dev.yml`                 | The two stateful services of the local stack: PostgreSQL 18 and SeaweedFS, digest-pinned, healthchecked, loopback-only, with the named volumes `pg-data` and `s3-data`                                                |
| `seaweedfs/s3-config.json`        | The local-only S3 identity (`evidgate-local-test-only`). Committed on purpose: it is accepted only by a local SeaweedFS container, is never a production credential, and is the file the 01.8.1-T1 parity suite reads |
| `seaweedfs/pending-lifecycle.xml` | The `pending/` 24 h lifecycle rule (T18) that `scripts/dev/up.sh` puts on the dev bucket                                                                                                                              |

Start it with [`../scripts/dev/up.sh`](../scripts/dev/up.sh) — never with a bare
`docker compose up`: the script exports `EVIDGATE_SEAWEEDFS_S3_CONFIG` (see below),
applies migrations and starts the hosts. `docs/dev-setup.md` is the quickstart.

Both images are pinned by multi-arch **image-index digest**, the same constants as
`tests/Shared/ContainerImages.cs`, so the dev stack and the Testcontainers suites
cannot drift:

- `postgres:18.6@sha256:5a5a84b19854a9ffaa54082c166ff4ec27473a361e496e5ea167f298f2da9722`
- `chrislusf/seaweedfs:4.47@sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882`

### Why no host path is bind-mounted here

`compose.dev.yml` mounts only named volumes, and the SeaweedFS identity file
reaches the container through the `EVIDGATE_SEAWEEDFS_S3_CONFIG` environment
variable (the container writes it to `/etc/seaweedfs/s3-config.json` before
starting the gateway). A bind mount of a repository file is not portable to the
machine this stack is developed on — measured 2026-09-26 on colima 0.x with
`mountType: virtiofs`, repo at `/Volumes/Project/Evidgate`:

```console
$ docker run --rm -v /Volumes/Project/Evidgate/infra:/probe:ro alpine:3 \
    sh -c 'ls -la /probe/seaweedfs; head -c 60 /probe/seaweedfs/s3-config.json'
drwxr-xr-x    2 root     root          4096 s3-config.json   # the host file arrives as a directory
head: /probe/seaweedfs/s3-config.json: I/O error
```

SeaweedFS then fails its own startup check
(`fail to load config file /etc/seaweedfs/s3-config.json: is a directory`), and
`docker compose configs:` with a `file:` source behaves the same way because
compose resolves it to a bind mount. Reading the same file into the environment
from the host (where it is a normal file) and writing it inside the container
keeps one source of truth for the identity and works on every host.

### Ports and data

| Service     | Endpoint (loopback only)              | Volume                            |
| ----------- | ------------------------------------- | --------------------------------- |
| `postgres`  | `127.0.0.1:5432` (`EVIDGATE_DB_PORT`) | `pg-data` → `/var/lib/postgresql` |
| `seaweedfs` | `127.0.0.1:8333` (`EVIDGATE_S3_PORT`) | `s3-data` → `/data`               |

PostgreSQL 18 keeps `PGDATA=/var/lib/postgresql/18/docker` and declares
`/var/lib/postgresql` as the image volume; mounting the pre-18
`/var/lib/postgresql/data` path would silently not persist anything. The
healthchecks are `pg_isready` over TCP (the entrypoint's temporary initdb server
listens on the unix socket only) and any HTTP response from the SeaweedFS S3
gateway — `docker compose up -d --wait` therefore gates `up.sh` without a sleep.

Volumes survive `scripts/dev/down.sh` and are deleted by `scripts/dev/up.sh --reset`.

### The dev bucket and the 24 h lifecycle

`up.sh` creates the `evidgate-dev` bucket and PUTs
`seaweedfs/pending-lifecycle.xml` over the S3 API, from inside the container
(its own curl, SigV4 via `--aws-sigv4`), then verifies the HTTP status. Measured
2026-09-26 against `chrislusf/seaweedfs:4.47`: `PUT /evidgate-dev?lifecycle=` →
`200`, and `GET ?lifecycle=` returns the same rule:

```xml
<LifecycleConfiguration><Rule><ID>evidgate-pending-24h</ID>
  <Filter><Prefix>pending/</Prefix></Filter><Status>Enabled</Status>
  <Expiration><Days>1</Days></Expiration></Rule></LifecycleConfiguration>
```

So the emulator does support the per-prefix expiry T18 asks for; the rule deletes
staging objects under `pending/` after 24 h on the dev stack. (The provider-side
smoke test of the same behaviour stays with 10.6.1-T2 — local green does not
replace it.)

# Container images (10.2.1-T1)

`infra/docker/` holds the three deployable images of the area plan §3 (ruling
T41): `Dockerfile.api`, `Dockerfile.worker` and `Dockerfile.migrator`, each a
multi-stage `dotnet publish` build on one pinned builder. `apps/web` is not a
container — it is a static bundle on object storage + CDN (AC6, carried by
10.2.1-T1.4).

Build and check them with `scripts/build-images.sh`. It needs BuildKit
(`FROM --platform=$BUILDPLATFORM` and the automatic `TARGETARCH` argument are what
make one Dockerfile serve linux/amd64 and linux/arm64, 00 §11); a
docker-container builder cross-publishes the other architecture without emulating
the SDK:

```console
docker buildx create --name evidgate --driver docker-container --use   # once
scripts/build-images.sh                                    # host platform, all three
scripts/build-images.sh --platform linux/amd64 --platform linux/arm64
scripts/build-images.sh --image api --with-db               # + throwaway PostgreSQL
scripts/build-images.sh --check-pins                        # audit the pins, print them
scripts/build-images.sh --check-pins --check-registry       # re-resolve the tags (network)
```

The checks it runs are the acceptance criteria, not a rerun of the build:
`docker inspect` says `Config.User=1654` (non-root, AC1); the built image's first
layers are the pinned runtime base's layers for _both_ platforms (AC1 — the
comparison target is read from the registry manifest, because a local multi-arch
pin resolves to one platform image; the Api is compared against its `-extra`
base and Worker/Migrator against theirs); the published `runtimeconfig.json`
carries `System.GC.Server` plus the per-image `System.Globalization.Invariant`
(`true` for Worker, `false`/absent for the Api) and the main assembly has a
ReadyToRun header (00 §5.7 P7); the baked expected-migration-head is the
repository's head (01.1.2-T1.3); and `docker run <migrator> --help` prints the
01.4.1 §5 CLI (AC5). `--with-db` adds a throwaway PostgreSQL 18 (the
compose.dev.yml pin): `--status` on an empty journal, a real apply, `--status`
again, then the Api answering `/internal/health/live` and `/ready`.

## Pins

Base images are pinned to mcr.microsoft.com **manifest-list digests**, resolved on
2026-09-27 and — for the Api's `-extra` runtime base — on 2026-10-23
(reproducible: `GET https://mcr.microsoft.com/v2/<repo>/manifests/<tag>`
with `Accept: application/vnd.docker.distribution.manifest.list.v2+json` and read
the `Docker-Content-Digest` header; the per-platform manifests and their configs
then come from `/manifests/<digest>` and `/blobs/<config digest>`):

| Role                     | Tag + digest (the Dockerfile's `FROM`)                                                                                                             | Per-platform manifests (amd64 / arm64)  |
| ------------------------ | -------------------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------- |
| Builder                  | `mcr.microsoft.com/dotnet/sdk:10.0.100@sha256:c7445f141c04f1a6b454181bd098dcfa606c61ba0bd213d0a702489e5bd4cd71`                                     | `sha256:b84b696e…` / `sha256:4c85fffe…` |
| Runtime (`api`)          | `mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled-extra@sha256:00e0ad6a7ef8c0c1391b87f05c7ac757a15740455688f2bfcd146a3f4b987efd`               | `sha256:69c87e30…` / `sha256:c4b3500d…` |
| Runtime (`worker`, `migrator`) | `mcr.microsoft.com/dotnet/aspnet:10.0.12-noble-chiseled@sha256:9651fa59abcdf177c30392cb44a820605ca5d618429ab37acbf6e7c644510b02`               | `sha256:0053105f…` / `sha256:46587ed1…` |

- The builder tag equals `global.json`'s `sdk.version` — the SDK CI installs — so
  the image and the test build compile with one toolchain. `--check-pins` fails
  when the two drift; `--check-registry` reports when upstream publishes a new
  patch under the same tag (informational: the digest is the pin).
- Both runtime bases are chiseled: no shell, no package manager, and
  `Config.User=1654` (the base's `APP_UID`), which the Dockerfiles re-assert with
  `USER $APP_UID` so the contract survives a base swap.
- **The Api runs the `-extra` variant and Worker/Migrator the plain one.** The
  plain chiseled base is globalization-invariant by construction: it sets
  `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` and ships no `libicu` and no
  `zoneinfo`. Authoring text must be stored composed (13.1.1 AC14) and
  `string.Normalize(FormC)` is a *silent no-op* in invariant mode, so the Api
  cannot run that base: `Directory.Build.props` keeps `InvariantGlobalization`
  for `Evidgate.Worker` only, and `Dockerfile.api` pins the `-extra` image.
  `-extra` is the same distroless chiseled image plus `icu` and `tzdata`
  ([dotnet-docker image variants: "`extra` images contain everything that the
  default images do, plus `icu` and `tzdata`"][variants]); measured 2026-10-23 by
  exporting both arm64 filesystems, the plain base has 0 `libicu` / 0 `zoneinfo`
  entries and the `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=true` variable, while
  `-extra` has the 12 ICU shared objects (`libicu{data,i18n,io,test,tu,uc}.so.74`)
  and 1 246 `zoneinfo` entries and sets no such variable.
  `scripts/build-images.sh --check-pins` fails if the Api base stops being the
  `-extra` sibling of the Worker/Migrator tag, and the P7 check fails if the
  Api's runtimeconfig or image environment is invariant.
- Licence: the .NET container images are MIT — [dotnet/dotnet-docker `LICENSE`]
  (MIT, © .NET Foundation) — with the container legal notice in the README's
  [License section] (`aka.ms/mcr/osslegalnotice`) and the Linux image contents
  under their own licences ([image-artifact-details.md]). The Ubuntu 24.04
  ("noble") packages inside both chiseled bases are covered by that same OSS
  notice; the two library sets `-extra` adds carry their own terms, read from the
  image itself (`usr/share/doc/<package>/copyright`): **ICU** (`libicu74`) under
  the **Unicode License V3** (SPDX `Unicode-3.0`, MIT-style — the Ubuntu package
  states `License: MIT`, © 1991-2022 Unicode, Inc.; [upstream `LICENSE`][unicode-licence])
  and **tzdata** in the public domain (`License: public-domain`, © IANA,
  `https://www.iana.org/time-zones`).
- `10.0.12-noble-chiseled` and (at resolution time) `10.0-noble-chiseled`
  resolved to the same digest `sha256:9651fa59…`; the versioned form is pinned so
  a runtime patch bump is a deliberate edit. The `-extra` pin follows the same
  rule: `10.0.12-noble-chiseled-extra` is pinned by its index digest
  `sha256:00e0ad6a…` (amd64 `sha256:69c87e30…`, arm64 `sha256:c4b3500d…`,
  arm/v7 `sha256:330dedb6…`), and a bump moves both bases together.

[dotnet/dotnet-docker `LICENSE`]: https://github.com/dotnet/dotnet-docker/blob/main/LICENSE
[License section]: https://github.com/dotnet/dotnet-docker/blob/main/README.md#license
[image-artifact-details.md]: https://github.com/dotnet/dotnet-docker/blob/main/documentation/image-artifact-details.md
[variants]: https://github.com/dotnet/dotnet-docker/blob/main/documentation/image-variants.md
[unicode-licence]: https://github.com/unicode-org/icu/blob/main/LICENSE

## Build inputs the context must keep

`Dockerfile.<name>.dockerignore` trims the repository-root context. Three inputs
of `src/` projects live outside `src/` and must stay in it, or the image is wrong
rather than failing to build:

| Path                           | Read by                                                                                                                     | Failure mode when absent                                                                                                                                                                             |
| ------------------------------ | --------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `contracts/rls-exceptions.txt` | `BuildingBlocks.Persistence` (`EmbeddedResource`, 02.6.1-T1.4)                                                              | the compile fails (measured 2026-09-27)                                                                                                                                                              |
| `migrations/*.sql`             | `BuildingBlocks.Persistence` bakes the greatest file name into assembly metadata as the expected journal head (01.1.2-T1.3) | silent: the head degrades to the `(none)` sentinel and every ready probe reports unready — `scripts/build-images.sh` asserts the baked head against the repository head                              |
| `.editorconfig`                | every project (analyzer severities)                                                                                         | silent: the container applies the analyzers' default severities, so a rule the repository downgraded (`VSTHRD200 = suggestion`) becomes an error under `TreatWarningsAsErrors` (measured 2026-09-27) |

The same ignore lists keep the git-ignored host state **out** of the context:
`.env*`, `.local`, `.husky`, `**/playwright-report`, `**/test-results` and
`**/*.tsbuildinfo`. `.env.dev` is the one that matters — `scripts/dev/up.sh` writes
real per-role passwords into it (00 §5.7 S3) and the context is uploaded to the
builder, so it must not carry credentials even though the Dockerfiles `COPY` named
paths only. Measured 2026-09-27 with a scratch `FROM scratch` stage: all three were
in the context before these patterns were added, and are rejected by
`.dockerignore` after; `scripts/build-images.sh --check-pins` now fails when a
regenerated list drops a pattern.

`dotnet restore` inside the build runs in locked mode against the committed
`packages.lock.json` files, with `-p:PublishReadyToRun=true` (since .NET 6 the R2R
runtime assets must be part of the restored graph, or the publish fails with
NETSDK1094). `Directory.Build.props` declares
`RuntimeIdentifiers=linux-x64;linux-arm64` for every `src/` project, which is what
lets `dotnet publish -r linux-<arch>` run without a RID-specific restore while the
lock files stay independent of the machine that generated them.

Precisely because of that property, do not run the R2R restore on a macOS
workstation: `-p:PublishReadyToRun=true` makes the SDK add the _host_ RID
(`osx-arm64`) to the runtime-identifier set, and locked mode then refuses the
restore ("runtime identifiers have changed from … osx-arm64"). Inside the image
the host is linux/amd64 or linux/arm64 — both already declared — so the Dockerfile
restores in locked mode on either runner. Restore RID-lessly on the host (the
verify stage does), and build the images through `scripts/build-images.sh`.

## Operational note: the Npgsql GSSAPI load line

The first database connection in these images logs two lines to the console:

```console
Cannot load library libgssapi_krb5.so.2
Error: libgssapi_krb5.so.2: cannot open shared object file: No such file or directory
```

That is Npgsql probing for the optional Kerberos native library at driver
initialisation; neither chiseled base ships `libgssapi-krb5-2` — the Api's
`-extra` base adds ICU + tzdata only (measured 2026-10-23: no `libgssapi`/`libkrb5`
file in either image), so **the Api now runs `-extra` and still logs these two
lines**; `-extra` is not a remedy for them. Password authentication is unaffected
— measured on the built images 2026-09-27 (plain base): the Migrator applied the
whole migration set (eleven files at that measurement) and the Api reported
`{"status":"ready","journal_head":"0011_identity_membership.sql"}` with the same
session in which those lines appear. It is log noise, not a failure path; if
10.3.1's log review wants it gone, the only option is copying the Kerberos runtime
libraries into the runtime stage.

## Worker Node runtime (ruling T36) — carried

`packages/healing-validator` (the TypeScript AST validator / Playwright healer)
does not exist yet, so `Dockerfile.worker` carries **no** Node layer: 08.4.1-T3.3
owns that pin and the spawn path, and a layer with no consumer could not be
verified (nothing to run, nothing to assert). Add `node:26` (digest-pinned) to the
worker's runtime stage there, next to these pins.
