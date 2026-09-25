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
