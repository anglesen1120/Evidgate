# Review deployment runbook

Status: **Pilot-0 review deployment (S188) — written with the lane; no ingress mode has been exercised against a live EC2 host or a real Cloudflare account yet** ⚠️. The stack is in [`../../infra/review/`](../../infra/review/compose.review.yml), the commands in [`../../scripts/review/`](../../scripts/review/lib.sh); this file is the operator procedure. Not a production deployment: hosts run in Development mode, the data is synthetic, and the only perimeter is Cloudflare Access (named tunnel) or HTTP Basic Auth (quick tunnel).

Legend as in [`../../AGENTS.md`](../../AGENTS.md): ✅ verified with a link · ⚠️ unverified or blocked today · `[INFERENCE]` · `[DECISION]` · `[OPEN]`.

## 1. Purpose, audience and rules

Purpose: put the Pilot-0 build (API, worker, web) on one Linux host at an HTTPS URL, so a reviewer can open the run list, a run, its evidence panel and its decision, and nothing else can reach it. Audience: the operator with SSH to the host. A Cloudflare account and a domain are needed for the named tunnel; quick mode needs neither and is the short-demo path (§4.1).

- **Pick one ingress mode and keep it** (§4.0). `--tunnel quick` puts the build on a public `*.trycloudflare.com` URL with **no Access in front**: Basic Auth on every path is the whole perimeter, and the stack refuses to serve quick mode without credentials (§2, §4.1).
- **Named tunnel only: Access application and allow-list policy first, public route second** (§4.4, §4.5). A route without an Access application is open to anyone, and Access is deny-by-default ✅ ([self-hosted app](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/self-hosted-public-app/)).
- **Nothing is published on a host port in either tunnel profile.** `web`, `api` and `worker` are reachable only inside the internal `evidgate-review-net` bridge; only `cloudflared` / `cloudflared-quick` also joins an egress network, and it is the ingress ✅ (`../../infra/review/compose.review.yml`). `ss -ltn` must show no `0.0.0.0:8080`, `:5432`, `:8333`.
- **Not for real customer data.** The seed creates one org, one project and one dev user (§5); retention, backups and per-tenant isolation are not exercised here.
- **Secrets never leave the host through a terminal you do not control.** `.env.review` is mode `600`, generated once, and the scripts print its path — never its contents. Do not paste the dev-login secret or the Basic Auth password into chat, a ticket or a screenshot.
- **The hosts stay in Development mode** (`seed-dev` refuses any other environment and the dev sign-in route is Development-only), so the ingress credential layer is the perimeter (`../../infra/review/env.review.template`).

## 2. What runs on the host

Compose project `evidgate-review`; every container and volume is named `evidgate-review-<service>`. Base file `../../infra/review/compose.review.yml`, with `../../infra/review/compose.quick.yml` layered on for the quick tunnel.

- `postgres` — the dev stack's pinned PostgreSQL 18 on a volume, no host publish; data survives `down.sh`, dies with `--purge --yes`. `seaweedfs` — same pin, its S3 identity handed over as `EVIDGATE_SEAWEEDFS_S3_CONFIG`, no bind-mounted repository file (why: [`../../infra/README.md`](../../infra/README.md)).
- One-shots: `db-init` (`bootstrap` creates the owner role, the database and the application roles as `scripts/dev/db-init.sh` does, then `grants`), `migrator` (`infra/docker/Dockerfile.migrator`, applies migrations and is the seed entry point — `docker compose run --rm migrator seed-dev [--with-token]`), `bucket-init` (creates the `evidgate-review` bucket and PUTs the dev stack's 24 h `pending/` lifecycle rule from inside the network), `probe` (polls readiness from inside).
- `api` (`infra/docker/Dockerfile.api`, port 8080 inside the network only) and `worker` (`infra/docker/Dockerfile.worker`, no port).
- `web` — `../../infra/review/Dockerfile.web`: the built `apps/web` bundle (en + vi) on nginx on 8080, same-origin, reverse-proxying `/v1/` to `http://api:8080`, answering `404` for `/internal/*` (the API's operator and probe surface never leaves the network), and Basic Auth on every path, so a credential-less request gets `401` before any proxying. Its start script writes `/tmp/evidgate/htpasswd` from user + apr1 hash and refuses to serve quick mode without them.
- `web` healthcheck: `wget -q -O /dev/null http://127.0.0.1:8080/nginx-health`. `/nginx-health` is `auth_basic off` plus `allow 127.0.0.1; deny all;`, so it answers only from inside the container and `403` to a request that arrives through the tunnel.
- `cloudflared` — profile `tunnel`: `tunnel --no-autoupdate run`, token from `CLOUDFLARE_TUNNEL_TOKEN`, no ports, hostname from the dashboard route (§4.5). `cloudflared-quick` — profile `tunnel-quick`: `tunnel --no-autoupdate --url http://web:8080`, no token and no dashboard; its ephemeral URL is read from `docker logs evidgate-review-cloudflared-quick`.

Every service has a compose `mem_limit`/`mem_reservation` sized for the 4 GB host — ≈2.9 GB of limits in total: postgres 512m (`shared_buffers` 128 MB, 50 connections), seaweedfs 384m, api 640m, worker 384m, web 64m, cloudflared 96m, migrator 512m, db-init/bucket-init 128m, probe 64m, plus `DOTNET_gcServer=0` and heap caps in api and worker. Measured on the local smoke: `measured idle RSS: <filled after local smoke>`.

Images are tagged `EVIDGATE_REVIEW_TAG` (the release sha by default) as `evidgate-review-{api,worker,migrator,web}:<tag>` with `pull_policy: never`, so a host that never builds can receive them via `docker save | docker load`.

Profiles: `tunnel` / `tunnel-quick` are the public deployments (one cloudflared variant joins the stack, nothing is published on the host, the ingress always lands on `http://web:8080`, so nginx is the only door). `local` / `debug` publish through separate services (`web-local`, `api-debug`) because a compose port mapping is not profile-conditional: `local` adds `web-local` on `127.0.0.1:${EVIDGATE_REVIEW_LOCAL_PORT:-8088}`, `debug` adds `api-debug` on `127.0.0.1:${EVIDGATE_REVIEW_API_PORT:-18080}` for the dogfood window and by-hand probes.

## 3. Host requirements

Reference host: **AWS EC2 `t4g.medium`** — `arm64`, Ubuntu 24.04, user `ubuntu`, 2 vCPU, 4 GB RAM, 30 GB `gp3`. Security group: **no inbound rule except SSH from the operator's IP**, outbound 443 (image pulls, Cloudflare edge).

- Linux `x86_64` or `aarch64`/`arm64` (the base images are multi-arch from one pinned builder; pins in [`../../infra/README.md`](../../infra/README.md)); `deploy.sh` reads `uname -m` and refuses others. Docker Engine with Compose v2, reachable by the SSH account without an interactive re-login.
- **At least 15 GB free** under the SSH account's `$HOME`: `deploy.sh` measures `df` there and refuses below that. On the 30 GB reference disk, prune images between releases. `$HOME` must be writable: the release tree, the secrets file and `current` live under `$HOME/evidgate-review/` (override with `EVIDGATE_REVIEW_HOST_HOME` or `EVIDGATE_REVIEW_HOST_ROOT` on the operator side).
- **2 GB of swap.** 4 GB RAM runs the stack and cannot also build three .NET images. `deploy.sh --setup-swap` creates a 2 GiB `/swapfile` when the host has under 1 GiB, needs passwordless `sudo`, and is idempotent (existing file reused, one `fstab` line). By hand:

  ```console
  sudo fallocate -l 2G /swapfile || sudo dd if=/dev/zero of=/swapfile bs=1M count=2048
  sudo chmod 600 /swapfile && sudo mkswap /swapfile && sudo swapon /swapfile
  echo '/swapfile none swap sw 0 0' | sudo tee -a /etc/fstab
  ```

- **Build on the operator machine**: `--build-local` (§5) builds the four images from the exact sha for the host's platform and ships them with `docker save | docker load`; the host only loads. The default host-side build is too heavy for 4 GB and is the path a bigger host may take.
- `curl`, `python3` and `node` ≥ 20 on the host as well: the gates in §5 run there. The operator machine needs `git`, `curl`, `ssh` and `docker` with `buildx`, and a clone holding the sha (§5, §7).
- **No inbound port**: no security-group rule beyond SSH, no `ufw allow` (§10.4 catches it).

## 4. Ingress setup

### 4.0 Choose the mode

| Mode | Flag | Front door | Needs | Lifetime |
|---|---|---|---|---|
| quick | `--tunnel quick` (`tunnel-quick`) | Basic Auth on every path | outbound 443 only | demo: new URL on every restart |
| named | `--tunnel token` (default, `tunnel`) | Cloudflare Access (OTP) | Cloudflare account + domain | stable hostname, the review default |
| none | `--tunnel none` (no origin given) | — | — | local / ssh-only checks |

`EVIDGATE_REVIEW_TUNNEL_MODE` in `.env.review` records the choice (default `token`). `[DECISION]` quick mode exists so the Pilot-0 build can be shown before a domain is in the account: short demo, not the review deployment.

### 4.1 Quick tunnel: no account, Basic Auth instead of Access

```console
scripts/review/deploy.sh <ssh-host> --tunnel quick --build-local --setup-swap
```

- `cloudflared-quick` opens a throwaway tunnel to `http://web:8080` and gets a random `https://<words>.trycloudflare.com` hostname; `deploy.sh` prints it, `status.sh` reprints it from the log (`docker logs evidgate-review-cloudflared-quick | grep -o 'https://[a-z0-9-]*\.trycloudflare\.com'`).
- **No Cloudflare Access and no account**: anyone with the URL reaches the edge, so `compose.quick.yml` forces `EVIDGATE_REVIEW_BASIC_AUTH=required`, and the image's start script independently refuses to serve quick mode without credentials — a quick tunnel can never be unauthenticated. No public origin is passed, so the CSP keeps `connect-src 'self'`.
- Credentials are generated into the host's `.env.review` (mode `600`) on the first deploy: user `reviewer`, a random password (plaintext kept for the owner to hand out; no container receives it) and its apr1 hash (the only form a container sees). `deploy.sh` prints the user and the file path, never the password.
- The URL is **ephemeral**: restarting `cloudflared-quick`, redeploying or rebooting hands out a new hostname and the old one dies. Do not bookmark it. `[OPEN]` nothing in the stack can pin or name it; that is what the named tunnel is for.

### 4.2 Named tunnel: create it and its token

**Networking** > **Tunnels** > **Create a tunnel** ✅ ([create a tunnel](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/get-started/create-remote-tunnel/)). Older notes say "Networks > Tunnels", the current page says **Networking** > **Tunnels** ⚠️; direct link `https://dash.cloudflare.com/?to=/:account/tunnels`. Name it `evidgate-review`, choose **Docker** as the connector platform, copy the install command: the token is the long string after `--token` ⚠️ (the Docker variant carries `tunnel --no-autoupdate run --token …`, which is what compose runs).

`deploy.sh` reads that token from `CLOUDFLARE_TUNNEL_TOKEN` in the **operator's** shell — never from a flag, because `argv` is visible in `ps` — and writes it into the host's `.env.review` on the first deploy, where it is never printed and never rewritten. Do not commit it. Leave the tunnel **without** a public hostname until §4.5.

### 4.3 Add the One-time PIN identity provider

One-time PIN is **not** added automatically for new Zero Trust organisations: **Zero Trust** > **Integrations** > **Identity providers** > **Add new identity provider** > **One-time PIN** ✅ ([one-time PIN](https://developers.cloudflare.com/cloudflare-one/integrations/identity-providers/one-time-pin/)). The PIN is single-use, expires 10 minutes after the request, a new PIN invalidates the old one, and mail goes only to an address a policy allows ✅ (same page). Sender is `noreply@notify.cloudflare.com`; a scanner that follows links consumes the PIN — request a new code.

### 4.4 Create the Access application and the allow policy

**Zero Trust** > **Access controls** > **Applications** > **Create new application** > **Self-hosted and private** > **Add public hostname** ✅ ([self-hosted application](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/self-hosted-public-app/)).

- **Domain**: the review hostname (`evidgate-review.<your-domain>`), matching §4.5 exactly; the zone must be active in the same account.
- **Access policies**: one **Allow** policy whose include rule is an e-mail list (the selector's label is ⚠️ unverified) with every reviewer address plus your own. Add no `Bypass`: Access is deny-by-default, so an unlisted address cannot sign in even with a valid PIN.
- **Authentication**: enable the One-time PIN provider from §4.3 and leave **Apply instant authentication** off — with one provider it would skip the login page, and that page is what §10.1 proves.
- **Session Duration**: `24 hours`, the default ✅ ([session management](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/session-management/)). That is the application token for this hostname; the global session (default 24 h, 15 min to one month) bounds PIN re-entry. `[DECISION]` 24 h: a review day, not a week.

### 4.5 Add the public hostname route (last)

**Networking** > **Tunnels** > `evidgate-review` > **Routes** > **Add route** > **Published application** ✅ (same create-tunnel page as §4.2). **Subdomain / domain**: the hostname of §4.4. **Service URL**: `http://web:8080` — the compose service, plain HTTP inside the bridge network. Not `https://`, not the host's loopback: `cloudflared` is a container on `evidgate-review-net`. Save; Cloudflare creates the DNS record for a full zone setup, and with a partial (CNAME) setup you create the `CNAME` at your own provider ✅ (same page).

The route is the moment the hostname resolves, which is why it comes after the Access application. If §4.4 was skipped, stop — the app is public. Pass `--public-origin https://<hostname>` on the first deploy to extend the CSP's `connect-src` to that origin; without it the policy stays `'self'` and the hostname is only whatever the dashboard route serves ⚠️.

Optional hardening, **not** in the shipped compose ⚠️: the tunnel's origin parameters can validate the Access JWT themselves ("Protect with Access"), so a request that bypasses the edge is rejected at `cloudflared` ✅ ([origin parameters](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/origin-parameters/#access)). Dashboard change on the public hostname, untested here — prove it before trusting it.

## 5. First deploy

Run from a clone on the operator machine, never on the host. The tree must be clean: the release is `git archive <sha>` and nothing else, so an uncommitted edit would silently not ship.

```console
scripts/review/deploy.sh <ssh-host> --tunnel quick --build-local --setup-swap   # demo, no domain
scripts/review/deploy.sh <ssh-host> --tunnel token --public-origin https://evidgate-review.example.com \
  --build-local --setup-swap                                                   # named tunnel
```

`--build-local` builds `migrator`, `api`, `worker` and `web` here for `linux/<host arch>` from an extraction of the exact sha (`docker buildx` required, QEMU when the architectures differ), then ships them as a compressed `docker save` stream the host only loads.

Flags: `--sha <sha>` (default `HEAD`), `--tunnel quick|token|none`, `--public-origin <https-url>`, `--build-local`, `--setup-swap`, `--no-dogfood`, `--dry-run`, `--local`. Refusals: `--tunnel quick` takes no `--public-origin`; `--local` takes no host, no tunnel and no `--build-local`; a first `--tunnel token` deploy without `CLOUDFLARE_TUNNEL_TOKEN` in the shell stops before the secrets file exists (§5.1), as does quick mode with a file that has no auth hash.

`--dry-run` prints the thirteen-step plan and runs only the read-only preflight (§3). The real run:

1. preflight over ssh (§3), including the memory/swap report and, with `--setup-swap`, the swap file;
2. resolve the release: `--sha` or `HEAD`, a full 40-character commit that is an ancestor of `HEAD` and holds `infra/review/compose.review.yml` and `infra/review/env.review.template`;
3. secrets: create `$HOME/evidgate-review/.env.review` **once** from the template in the shipped commit, mode `600` (§5.1); a second run reports "kept existing" and rewrites nothing;
4. ship: `git archive <sha>` unpacked into `releases/<sha>/` beside a `.release-sha` marker (reused only when its marker reads the same sha);
5. gates and images: `infra/review/check.sh --static-only` (pins and inlined files) before a layer is built, then `docker compose build` — or, with `--build-local`, the build here plus `docker save | docker load` — then `infra/review/check-web-image.sh --env-file "$HOME/evidgate-review/.env.review"` (two `nginx -t` runs plus three fail-closed Basic Auth cases);
6. start `postgres` and `seaweedfs` with `up -d --wait`, so the start waits on their healthchecks;
7. schema: `db-init bootstrap`, `migrator`, `db-init grants`;
8. object store: `bucket-init` creates the bucket and applies the 24 h `pending/` lifecycle rule — the same rule, not a second implementation;
9. seed: `seed-dev --with-token`. Its `EVIDGATE_DEV_TOKEN=` line is deleted before anything is printed, and the token plus the suite / case / environment ids stay in shell variables;
10. serve: `up -d --wait api worker web` (`web-local` on `--local`), then the one-shot `probe` polls `/internal/health/ready` on `api` and `worker` — the chiseled Api image has no shell or curl, so it cannot healthcheck itself, and `web` waits on its own `/nginx-health`;
11. dogfood (§5.2): `--profile debug up -d api-debug`, an `ssh -L` forward of its loopback port, a readiness wait through it, the script, then `rm -sf api-debug`;
12. tunnel: `token` starts `cloudflared` and waits for the public origin to answer `200`/`3xx` (the `3xx` is the Access redirect expected before sign-in); `quick` starts `cloudflared-quick`, reads the URL from its log and waits for `401` or `200`/`3xx` there. Either way an assertion follows that no container publishes a host port;
13. `current` is repointed and the previous release is offered as the rollback command.

It prints the sha, the public URL (quick: the random host plus the warning that it changes on every restart), the Basic Auth user with the **path** of the secrets file, and the rollback command — never a secret value: the ingest token is dropped from the seed output.

### 5.1 The secrets file

`$HOME/evidgate-review/.env.review`, mode `600`, next to `releases/`. `deploy.sh` mints every placeholder of `infra/review/env.review.template`: a 32-byte CI pepper and 48-byte signing key as base64, the SeaweedFS key as `evg-review-<hex>` with a 32-hex secret, everything else (role passwords, dev-login secret, Basic Auth password) as 24 hex bytes, plus the apr1 hash of that password. `EVIDGATE_REVIEW_SHA` holds the **first** deploy's sha and `EVIDGATE_REVIEW_TAG` the tag the images carry; later runs pass the sha they serve as a variable that wins over the file, so a hand-run compose command passes `EVIDGATE_REVIEW_SHA=<sha>` itself. It adds `EVIDGATE_SEAWEEDFS_S3_CONFIG` (identity JSON, so the container writes it instead of bind-mounting), the tunnel mode, the Basic Auth keys, `EVIDGATE_REVIEW_PUBLIC_ORIGIN`, `CLOUDFLARE_TUNNEL_TOKEN` and the loopback ports later runs read back. `COMPOSE_PROJECT_NAME` pins the project.

The Basic Auth keys are load-bearing: `EVIDGATE_REVIEW_BASIC_AUTH_USER` (default `reviewer`) and `EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD` (plaintext, only for the owner to hand out) stay in the file; compose passes only the user and the apr1 hash `EVIDGATE_REVIEW_BASIC_AUTH_HASH` (single-quoted, so `$apr1$` survives) — bcrypt is rejected on purpose, there is no `crypt()` in the nginx image. `EVIDGATE_REVIEW_BASIC_AUTH=required` refuses to start `web` without user and hash; `optional` enforces them the moment both are set and serves without auth only while both are empty. `CLOUDFLARE_TUNNEL_TOKEN` is the one value not minted: it comes from the operator's shell (§4.2).

It is written over stdin into a `umask 077` temporary and moved in with `mv -n`, so a concurrent deploy keeps the file that won and no value becomes an argv. Never copy it into the repository, a ticket or a chat message, and never let it appear in `docker compose config` output.

### 5.2 The dogfood run

The default flow runs `scripts/dev/pilot0-dogfood.sh` **from the operator machine** against the deployed API: the `ssh -L` forward of step 11 makes it a loopback address there, the only shape the script accepts (`EVIDGATE_API_URL`, default `http://127.0.0.1:8080`); the case id is read from the review database on the host (the dev token cannot read the catalog). Run A must gate PASS (0), run B FAIL (1), run C pending (2) — the check in [`pilot0-dogfood.md`](pilot0-dogfood.md) and item (d) of the S186 notes in [`../../plans/features/SOLO-PATH.md`](../../plans/features/SOLO-PATH.md).

The seed prints its inputs **only while it creates them**, so a redeploy against existing volumes prints neither the token nor the three `PILOT0_*` ids — and the token never reaches the terminal at all: `deploy.sh` deletes the one line that carries it, because a minted token's secret cannot be shown again. So a redeploy to a seeded stack passes `--no-dogfood` (it also suits an operator machine without `node_modules`/Playwright), or exports a token and ids the operator captured from their own `seed-dev` run; without either it stops and names exactly that, and the review deployment never depends on the dogfood result anyway.

`--local` runs the same steps here (no ssh, no tunnel, `web-local` instead of `web`, an `http://127.0.0.1:<port>/` URL) under a throwaway root it prints as `local smoke root: …` (`mktemp -d $TMPDIR/evidgate-review-local.XXXXXX`), so `$HOME` keeps nothing and every smoke mints fresh secrets; `EVIDGATE_REVIEW_HOST_ROOT` pins a path. `--local` refuses `--public-origin`.

## 6. How a reviewer signs in

**Quick tunnel:** the reviewer opens the printed `https://<words>.trycloudflare.com/` URL and the browser prompts for Basic Auth — user `reviewer`, password from the owner, who reads it on the host in their own terminal and delivers it out of band (password manager, not chat):

```console
ssh <ssh-host> "grep '^EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD=' \$HOME/evidgate-review/.env.review"
```

A wrong password answers `401` on every path. There is no Access page in this mode, and the browser keeps the credential for that origin, so the app's own sign-in below needs nothing extra.

**Named tunnel:** the reviewer opens `https://evidgate-review.example.com/`, gets the Access page, enters their email and selects **Send login code**; the PIN arrives from `noreply@notify.cloudflare.com` and is valid for 10 minutes ✅ (§4.3). After the PIN the `web` bundle loads: `/` answers `302` to `/vi/` (the default locale) and `/en/` is the English build, the routing `apps/web/e2e/static-server.mjs:8-16` implements.

**App sign-in, both modes.** The app has no human login yet (02.3.1 is open), so the review build uses the Development-only dev sign-in: `POST /v1/dev/session` with the secret from `.env.review`. The route exists only when `ASPNETCORE_ENVIRONMENT=Development` and both `EVIDGATE__DEVLOGIN__ENABLED=true` and `EVIDGATE__DEVLOGIN__SECRET` are set ⚠️ (parallel dev-login lane) — check that the deployed sha carries it (`404` means the sha predates it, `401`/`403` means environment or keys are wrong). It issues a real session cookie through the normal session machinery, which is what makes `GET /v1/runs/{r}` answer instead of 401. The reviewer, or the operator on their behalf, reads the secret on the host in **their own** terminal:

```console
ssh <ssh-host> "grep '^EVIDGATE__DEVLOGIN__SECRET=' \$HOME/evidgate-review/.env.review"
```

That prints the secret to that terminal and to no file; a reviewer without SSH asks the operator to deliver it out of band. The cookies are scoped to the hostname, and the reviewer then sees the run list, run detail, evidence panel and decision.

`[OPEN]` the Dev sign-in form lands with 02.3.1's session work; until then the web path is the `POST /v1/dev/session` call itself, which a reviewer cannot drive from the browser. The tunnel and credential layers do not depend on it.

## 7. Upgrade and rollback

Upgrade to a newer commit (the stack is seeded, so the dogfood has no token to use):

```console
scripts/review/deploy.sh <ssh-host> --no-dogfood
```

Add `--build-local` on the 4 GB reference host: it then loads images instead of building them. Pass `--tunnel quick` again to keep a quick tunnel — **restarting it hands out a new URL**, so tell the reviewer and expect the old link to fail. With no `--sha` this deploys `HEAD` of the clean tree; `--sha <sha>` ships a specific 40-character ancestor of `HEAD`. One `releases/<sha>/` is kept per sha (reused only when its `.release-sha` matches) and `current` is repointed; the previous one stays on disk, which is what makes a rollback possible.

Against existing volumes the seed prints neither the token nor the ids, and the token never reaches the terminal, so every redeploy and rollback passes `--no-dogfood` — or the operator exports a token and ids captured from their own `seed-dev` run (§5.2).

Rollback (code only, one step):

```console
scripts/review/deploy.sh <ssh-host> --sha <previous-sha> --no-dogfood
```

`<previous-sha>` is the `previous release:` line the last deploy printed and exists in the operator's clone (the clone deployed it); the host's `releases/<sha>/` is that same tree — nothing is rebuilt. Two limits ⚠️: **migrations are forward-only** (the migrator applies what is missing and never reverts, so an older API against a newer schema works only when the newer migration was additive; across a destructive one, recreate the stack with `down.sh --purge --yes` and deploy the old sha), and **rollback does not restore data** — volumes keep whatever the newer build wrote.

## 8. Status, stop, purge, teardown

```console
scripts/review/status.sh <ssh-host>
```

is read-only and safe at any time. It prints the compose project, the release `current` points at and whether its `.release-sha` marker still matches, the secrets file path with its mode (`ls -l`, never the content), the tunnel mode with its public URL (`cloudflared`'s dashboard hostname, or the `trycloudflare.com` URL from `cloudflared-quick`'s log), the Basic Auth user and the path of the file holding the password, every container with its state and status, and **every published host port**. `host ports: none published` is the expected line for either tunnel profile; anything else is a second door (§10.4). `--local` reports this machine's stack instead.

```console
scripts/review/down.sh <ssh-host>
```

runs `docker compose down --remove-orphans` through `current` (by project name when the shipped release is gone) and keeps the volumes: the next deploy of the same or an older sha serves the same data. A quick URL dies with it.

```console
scripts/review/down.sh <ssh-host> --purge --yes
```

also deletes the volumes (Postgres data and the bucket) and every `releases/<sha>/` plus `current`. `--purge` without `--yes` refuses: the volumes are the only copy and there are no backups. The **secrets file survives on purpose** (a later deploy reuses the credentials the services know, Basic Auth included), so deleting it by hand mints new role passwords and a new Basic Auth password, and purging the database volume is the only way to get a new dev ingest token out of the seed (§5.2).

Cloudflare teardown, in this order (§4.2 paths): **Networking** > **Tunnels** > `evidgate-review` > **Routes**: delete the published application route (the hostname stops resolving — first, when decommissioning); **Zero Trust** > **Access controls** > **Applications**: delete the application and its policy; **Networking** > **Tunnels**: delete the tunnel, which invalidates the token in `.env.review` (delete that file too if the host goes away); partial (CNAME) setups also need the DNS record removed at your own provider, a full setup's record goes with the route ✅. A quick tunnel needs no teardown: `down.sh` removes `cloudflared-quick`.

## 9. Rotating secrets

- **Tunnel token.** **Networking** > **Tunnels** > `evidgate-review` > **Rotate token** (the newer label; the permissions page calls it **Refresh token** ⚠️). Rotation blocks *new* connections with the old value but does not disconnect a running connector: replace the `CLOUDFLARE_TUNNEL_TOKEN=` value in `.env.review` (one line, mode `600` — a re-run never rewrites the file) and recreate the connector from the release that serves:

  ```console
  cd $HOME/evidgate-review/current
  EVIDGATE_REVIEW_SHA=$(cat .release-sha) docker compose --project-name evidgate-review \
    --env-file ../.env.review -f infra/review/compose.review.yml \
    --profile tunnel up -d --force-recreate cloudflared
  ```

  Then confirm the tunnel is **Healthy** (§10.6). If the token leaked, delete the tunnel's existing connections too, which drops every connector until the legitimate one reconnects ✅ ([tunnel tokens](https://developers.cloudflare.com/tunnel/advanced/tunnel-tokens/)).
- **Basic Auth password.** Mint a new password plus `openssl passwd -apr1 -stdin <<< '<new password>'` (apr1 only — bcrypt is rejected), update `EVIDGATE_REVIEW_BASIC_AUTH_PASSWORD` and `EVIDGATE_REVIEW_BASIC_AUTH_HASH` in the same edit (mode `600`), then `docker compose ... up -d web` (and `web-local` when it runs): the container rewrites `/tmp/evidgate/htpasswd` at start. Reviewers on the old password get `401` immediately; there is no session to expire, which is why quick mode has no "sign out".
- **Dev-login secret.** `EVIDGATE__DEVLOGIN__SECRET` is generated into `.env.review`; edit that one value (mode `600`) and `up -d --force-recreate api` the same way. `deploy.sh` never rewrites the file, so rotation is a manual edit and nothing else changes. Sessions signed with the old secret stop working, which is intended after a reviewer leaves.
- **CI pepper and signing key.** Same shape, recreating `api` and `worker`. A rotated pepper invalidates previously minted ingest tokens, and the seed prints a token only when it mints one (and `deploy.sh` suppresses that line) — purge the database volume (§8) to mint a new one.
- **Public origin.** `.env.review` holds the origin the **first** deploy wrote, a later deploy does not update it, and it is baked into the web image's CSP (`connect-src` is a build arg): moving the hostname needs the line edited on the host **and** `build web` before `up -d web`, with the Access application and route following. Quick mode has no origin: its CSP keeps `'self'` and the browser's Basic Auth credential covers the same origin.
- **Database and SeaweedFS credentials.** Changing them means `down.sh --purge --yes` and a fresh deploy: `db-init` creates the roles from the file, once.

## 10. Verification checklist

Run after a deploy and after any ingress change. Each row is a fact to observe, not a smoke test that "looks fine".

1. **Named tunnel: unauthenticated request is challenged.** `curl -sSI https://evidgate-review.example.com/` without cookies redirects to `https://<team>.cloudflareaccess.com/cdn-cgi/access/login/...` (`302`, the code Access uses ⚠️). A `200` with app HTML, or a redirect to the app's `/vi/`, means Access is not attached: take the route down and fix §4.4 first. This check must never be skipped.
2. **Quick tunnel: unauthenticated request is refused.** `curl -sSI https://<words>.trycloudflare.com/` answers `401` with `www-authenticate: Basic realm=…`, and the same request with `-u reviewer:<password>` answers `200`/`302` to `/vi/`. A `200` without credentials means the Basic Auth layer is missing: `down.sh`, fix `.env.review` (§5.1), deploy again.
3. **Headers.** Signed in (or with Basic Auth), `GET /en/` returns `content-security-policy` (`style-src` with `'nonce-<32 hex>'`, `connect-src` starting `'self'`), `strict-transport-security: max-age=31536000`, `x-content-type-options: nosniff`, `referrer-policy: no-referrer`, `frame-ancestors 'none'` inside the CSP, `cache-control: no-store` on the document and a long one on a hashed asset. The policy is generated at image build from `apps/web/scripts/csp.mjs` + `apps/web/scripts/app-origin-csp.json` — the same policy the e2e static server applies.
4. **Nothing is published on a host port.** `status.sh` prints `host ports      : none published`, and `ss -ltn` on the host shows no `:8080`, `:5432`, `:8333`. `deploy.sh` asserts it before reporting success, and `infra/review/check.sh` (docker) asserts it for both tunnel profiles.
5. **The right sha is running.** The same output prints `current` → `releases/<sha>` with a matching release marker, equal to the sha `deploy.sh` reported.
6. **Named tunnel is healthy.** **Networking** > **Tunnels** lists `evidgate-review` as **Healthy** ✅ (§4.2). `Inactive` means no edge connection: `docker logs evidgate-review-cloudflared`.
7. **The app works behind the ingress.** The run list renders, run detail shows the evidence panel and the decision, `/vi/` and `/en/` both load. A 401 from `/v1/runs/{r}` means the session cookie is missing (§6), not a broken proxy.
8. **The nonce is valid.** nginx writes `$request_id` into the header and into the body's `__CSP_NONCE__`, so the nonce is 32 hex characters rather than the 22-character base64 (`==`-padded) the e2e server generates; hex is in the base64 alphabet, so `'nonce-<32 hex>'` is a valid nonce source, and one value per response is what the policy requires. A literal `__CSP_NONCE__` in a body means `sub_filter` did not run (not `text/html`). `curl -sSI` shows `server: cloudflare` and no `server: nginx/x.y.z` (`server_tokens off`), and `/nginx-health` answers `403` from outside while `docker exec evidgate-review-web wget -qO- http://127.0.0.1:8080/nginx-health` answers `200`.
9. **The dogfood result** (from `deploy.sh` or a manual `scripts/dev/pilot0-dogfood.sh`): run A exits 0, run B 1, run C 2.

## 11. Known limits and risks

- **Quick mode has no Access in front.** One public `trycloudflare.com` URL plus one shared Basic Auth password is the whole perimeter: whoever the password reaches can use the app, and the URL lands in every log and chat it is pasted into. Short demo only; the named tunnel is the review deployment.
- **The quick URL is ephemeral.** Every `cloudflared-quick` restart, redeploy or reboot produces a new hostname; links already sent stop working and nothing in the stack can pin one.
- **Access is the only perimeter in named mode.** The app has no human authentication (02.3.1 open) and hosts run in Development mode. A mistake in §4.4 — a `Bypass` policy, a wrong hostname, a route without the application — exposes an app that can mint sessions; hence §4.5 and §10.1.
- **4 GB host: run, do not build.** Three .NET images next to the stack exhaust the machine, hence `--build-local --setup-swap` (§3). Memory limits stop one container taking the others down; they do not make the host bigger.
- **One host, no backups, no HA, no alerting ⚠️.** The volumes are the only copy, `--purge` is destructive, and a host loss needs a full redeploy.
- **No TLS to the origin.** `cloudflared` terminates public TLS and speaks plain HTTP to `web:8080` over the bridge: acceptable only while `web` is unpublished on a single-tenant net.
- **Development mode, synthetic data.** The dev sign-in, the seed and the relaxed diagnostics are Development-only behaviours behind the ingress; the seeded org, project, dev user, suite and policy prove nothing about multi-tenant isolation, retention or deletion.
- **Forward-only migrations** (§7): rollback is code, not schema. **The dev sign-in form is not shipped** (§6, `[OPEN]`): a browser reviewer cannot sign in until it lands.

## 12. Troubleshooting

- **`deploy.sh` refuses at preflight.** Read the line: disk below 15 GiB under `$HOME` (prune images and build cache), compose below v2, unsupported architecture, `$HOME` not writable, or ssh without docker rights. A swap warning names both remedies (`--setup-swap`, `--build-local`).
- **`--build-local` fails.** `docker buildx` must exist on the operator machine, and a different architecture needs QEMU (`docker run --privileged --rm tonistiigi/binfmt --install all`). The build runs from an extraction of the sha, so a failure there is a real build failure.
- **`deploy.sh` refuses the release.** Dirty tree, a sha that is not 40 characters, is not an ancestor of `HEAD`, or lacks `infra/review/compose.review.yml` (§7).
- **`deploy.sh` stops at the secrets step.** `.env.review` exists with a `__PLACEHOLDER__`; it is never rewritten, so fill it in on the host or move it aside and deploy again. With `--tunnel token`, an empty `CLOUDFLARE_TUNNEL_TOKEN` there stops the run; quick mode refuses a file without `EVIDGATE_REVIEW_BASIC_AUTH_HASH`.
- **`check-web-image.sh` fails a Basic Auth case.** The image must refuse to start with `EVIDGATE_REVIEW_BASIC_AUTH=required` and no user/hash, and must serve unauthenticated only with `optional` plus empty keys — a failure there means the start script, not the deployment, is wrong.
- **The dogfood dies.** The seed prints its token and ids only once (§5.2), so export them or use `--no-dogfood`; a dead forward means `127.0.0.1:18080` was busy — free it, or change `EVIDGATE_REVIEW_API_PORT` in `.env.review` and deploy again.
- **`cloudflared` restarts with 403/401.** The token belongs to a deleted tunnel or was rotated (§9); recreate the container after fixing the value.
- **`cloudflared-quick` logs no URL.** `docker logs evidgate-review-cloudflared-quick` shows the outbound error (egress 443/tcp or UDP 443 blocked). `deploy.sh` waits 180 s for the URL and fails with that excerpt.
- **Hostname resolves but the app 502s.** The route points at the wrong service; it must be `http://web:8080` (§4.5). `status.sh` shows whether `web` is up.
- **Access page loops.** The e-mail is not in the Allow policy, or the OTP mail was consumed by a scanner (§4.3). Blocked users get no mail by design.
- **Basic Auth rejects a correct password.** Password and hash must be regenerated together (§9) and stay single-quoted; then `--force-recreate web`. An apr1 hash pasted into the hash line without its single quotes is split by compose and never matches.
- **`/v1/` answers 404 behind the app.** The proxy passes `/v1/` unchanged; compare the API's routes (`GET /v1/runs`, `POST /v1/dev/session`).
- **`--local` passes but a tunnel profile does not.** `--local` publishes `web` on loopback; the tunnels also need egress to Cloudflare (the named one additionally DNS, Access and the route).

## 13. References

- [`../../infra/review/compose.review.yml`](../../infra/review/compose.review.yml) — the stack, profiles and pins; [`../../infra/review/compose.quick.yml`](../../infra/review/compose.quick.yml) — the quick-tunnel overlay that forces Basic Auth; `infra/review/check.sh --static-only` — pins and inlined files without a build (needs `node` ≥ 20 and `python3`; the flag-less run adds the rendered compose checks for ports and secret redaction).
- [`../../scripts/review/deploy.sh`](../../scripts/review/deploy.sh), [`status.sh`](../../scripts/review/status.sh), [`down.sh`](../../scripts/review/down.sh) — the operator commands of §5, §7 and §8, with `lib-host.sh` (host facts, swap, `--build-local`) and `lib-secrets.sh` (minting, Basic Auth hash) behind them.
- [`pilot0-dogfood.md`](pilot0-dogfood.md) — what the dogfood proves and how it fails; [`../../infra/README.md`](../../infra/README.md) — image pins, the no-bind-mount rule, CSP pairing.
- Cloudflare: [create a tunnel](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/get-started/create-remote-tunnel/), [self-hosted application](https://developers.cloudflare.com/cloudflare-one/access-controls/applications/http-apps/self-hosted-public-app/), [one-time PIN](https://developers.cloudflare.com/cloudflare-one/integrations/identity-providers/one-time-pin/), [session management](https://developers.cloudflare.com/cloudflare-one/access-controls/access-settings/session-management/), [tunnel tokens](https://developers.cloudflare.com/tunnel/advanced/tunnel-tokens/), [origin parameters](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/configure-tunnels/origin-parameters/#access), [connectivity pre-checks](https://developers.cloudflare.com/cloudflare-one/networks/connectors/cloudflare-tunnel/troubleshoot-tunnels/connectivity-prechecks/).
