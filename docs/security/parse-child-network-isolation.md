# Parse child: what "no network" can claim today (AC4 of 06.3.6)

Owner: 06.3.6-T1.4 (this delivery). Scope: the `Evidgate.Parser` child that the Worker spawns per parse
(`IParseRunner` / `ChildProcessParseRunner`, `src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/Runner/`).
Status of AC4 ("a parse attempting any outbound connection (DNS or TCP) fails inside the child and the attempt is
logged as a security event"): **partially met — the code and environment halves are proven, the runtime half is a
deployment item, and no security event can be emitted yet.** Unverified statements are marked `[INFERENCE]`.

Facts below were measured on 2026-10-28 with Docker 29.5.2 (linux/arm64 on darwin/arm64), using the repository's
own worker image `evidgate-worker:dev-arm64` and `mcr.microsoft.com/dotnet/sdk:10.0.100` for the syscall probes. The
dev stack (only PostgreSQL and SeaweedFS, `infra/compose.dev.yml`) was not started.

## 1. What the image and the runtime offer

| Question | Measured answer | Evidence |
|---|---|---|
| Can an unprivileged process create a network namespace? | **No.** `unshare --net` (needs `CAP_SYS_ADMIN`) and `unshare -rn` / `unshare -U` (`CLONE_NEWUSER`, optionally with `CLONE_NEWNET`) all fail with `Operation not permitted` — as root inside the container, under Docker's default (builtin) seccomp profile with no added capabilities. Non-root fails the same way: as the Worker's own UID 1654, `unshare -rn` = EPERM. | `docker run --rm --entrypoint /bin/bash mcr.microsoft.com/dotnet/sdk:10.0.100 -c 'id; unshare --net true'` → `unshare --net true` = EPERM, `unshare -U true` = EPERM; `-u 1654` → `unshare -rn true` = EPERM |
| Which gate refuses it — seccomp, the capability, or the kernel? | **Two container-level gates, neither the kernel.** The kernel is permissive (`kernel.unprivileged_userns_clone=1`, `user.max_user_namespaces=39517`). (a) Docker's default seccomp profile denies `clone`/`unshare` with `CLONE_NEWUSER` unless the caller holds `CAP_SYS_ADMIN`: with `--security-opt seccomp=unconfined` and **no** added capability, `unshare -U` and `unshare -rn` succeed (exit 0) while plain `unshare --net` still EPERM. (b) A plain `CLONE_NEWNET` needs `CAP_SYS_ADMIN`: with `--cap-add SYS_ADMIN` under the default profile, `--net` and `-rn` both succeed (exit 0). So either a relaxed seccomp profile (user-namespace route) or the capability (net-namespace route) removes the refusal. | the same image run four ways: default seccomp/no caps → `net=1, userns=1, usernet=1`; `seccomp=unconfined`/no caps → `net=1, userns=0, usernet=0`; default seccomp/`-u 1654` → `usernet=1`; `--cap-add SYS_ADMIN` → `net=0, usernet=0` |
| Could the child create a namespace itself? | **No.** The child is `dotnet /app/parser/Evidgate.Parser.dll`; the chiseled runtime image carries no shell and no util-linux: no `/bin/sh`, `/bin/bash`, `unshare`, `nsenter`, `setsid` or `ip` entry exists in the image filesystem. `dotnet` cannot call `setpgid`/`unshare` before `exec`, and the child holds exactly one allow-listed `DllImport` (`setrlimit`), enforced by `tests/Architecture.Tests`. | `docker create evidgate-worker:dev-arm64` + `docker export … \| tar -t` (no such paths); `infra/docker/Dockerfile.worker:81-104` |
| Does the Worker container get capabilities that would allow it? | `[INFERENCE]` no: the Dockerfile adds none and the deploy target is not in this repository. The runtime runs as UID 1654 (`docker inspect --format '{{.Config.User}}'` → `1654`, matching `USER $APP_UID`). | `infra/docker/Dockerfile.worker:96` |
| Does anything in the repository deny egress to the Worker? | **No.** The dev stack runs no Worker (`infra/compose.dev.yml` starts only `postgres` and `seaweedfs`), and `infra/staging/environment.json` and `infra/prod/environment.json` describe ingress paths, not container egress. | `infra/compose.dev.yml`; `infra/*/environment.json` |
| Is a fresh network namespace the fallback the S117 probe relies on? | The probe tries `unshare --net` and `unshare -rn` and **skips** with the helper's text when neither is possible — which is what the default profile above produces. | `tests/Parser.Tests/SandboxLimitsProbeTests.cs:253-335` |

The parse path itself needs no egress: the real image runs the child with networking removed entirely, streams its
NDJSON records and exits 0. So "no network" costs the child nothing; what is missing is the guarantee, not the
capability.

```sh
docker run --rm -i --network none --entrypoint /usr/bin/dotnet evidgate-worker:dev-arm64 \
  /app/parser/Evidgate.Parser.dll --profile xml --max-bytes 4096 --cpu-seconds 20 --max-file-bytes 0 \
  < fixtures/reports/junit/sample/input.xml
```

Consequence: per-child network-namespace isolation is **not available** in the V1 worker shape, and buying it is not
recommended on either route: `--cap-add SYS_ADMIN` is far broader than the need (mount, module, ptrace-adjacent
powers) and would be held by the process that runs hostile input, while `--security-opt seccomp=unconfined` trades
away the syscall filter that is the container's main sandbox for a namespace the child cannot even enter (its image
has no `unshare`).

## 2. What is enforced in code (proven, CI)

1. **The child has no networking API.** The compiled `Evidgate.Parser.dll` references no `System.Net*` assembly and
   contains no type reference or definition in a `System.Net*` namespace:
   `tests/Parser.Tests/ChildNetworkTests.cs` (`Child_Assembly_References_No_Networking_Type`) reads the assembly
   metadata, so a global `using`, a generated file or a suppressed analyzer cannot hide a use. The source-level rule
   that keeps it that way is `ParserChildIsolationRules.ForbiddenApis` (`System.Net`, `HttpClient`, `Socket`,
   `TcpClient`, `Dns.`, plus the implicit-using network types) with its lock-file prefix denial of `System.Net`
   packages, covered by `tests/Architecture.Tests/ParserChildIsolationTests.cs:145-201` and enforced on the real
   sources by `Real_Parser_Sources_Use_No_Forbidden_Api_And_No_Child_Io`. That rule is honest about its basis: it
   scans source text plus the project file and lock file, so it proves "no such source line and no such package" and
   not "no such IL reference" — the assembly-metadata test above is the IL-level half, and neither half stops a
   native syscall that no managed type exposes.
2. **The child inherits no network configuration.** `ChildEnvironment` builds a cleared environment holding exactly
   `DOTNET_GCHeapHardLimit`, `TMPDIR` and (when its runtimeconfig cannot declare it)
   `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT`; no proxy, CA-bundle, resolver (`RES_OPTIONS`, `LOCALDOMAIN`,
   `HOSTALIASES`) or `DOTNET_SYSTEM_NET*` variable travels. Asserted both on the builder and end to end against a
   spawned child that dumps its own environment (`ChildNetworkTests`
   `Runner_Child_Environment_Holds_Only_The_Allow_List`, `Spawned_Child_Environment_Carries_No_Network_Configuration`,
   which sets `HTTPS_PROXY`/`https_proxy` in the test process and asserts the child's own dump lacks them, so the
   assertion cannot pass merely because the host configures no proxy).
3. **Nothing else to leak.** The child receives one artifact's bytes on stdin, has no connection string and no
   credential in its environment, so even a hypothetical network call would authenticate as nobody.

## 3. What remains a deployment item

| Gap | Receiver | Why it is not code |
|---|---|---|
| A runtime guarantee that an outbound attempt from the Worker's process tree fails (and the network-namespace deny, if it is ever bought with a capability or a separate pod) | **10.2.1-T1.1** (image runtime contract; the concrete run-time policy lands with the deploy pipeline 10.2.2-T1) | Namespace creation is refused by the container profile (§1) and the Worker genuinely needs network for PostgreSQL, object storage and its queue, so a blanket `--network none` on the Worker is wrong. The shaped answer is an egress-deny network policy limited to the endpoints the Worker needs, or a dedicated parser runtime that can be network-isolated — both are deployment artefacts. |
| A security event for a blocked (or even attempted) outbound connection | **11.5.1-T5.1** (creates `security_event`) with the type set owned by 11.1.1-T4.1 | No `security_event` table or emitter exists yet (`docs/security/security-event-baseline.md`, "No row exists yet"), and the V1 closed type set has no "parse network attempt" member. AC4's "logged as a security event" half therefore cannot be met by 06.3.6 and must not be claimed. |

Where the runtime half *can* be asserted today is a container started with `--network none`:
`ChildNetworkTests.A_Process_Started_Like_The_Child_Reaches_Loopback_But_Has_No_Egress_Path` spawns a process
through the production runner's own path (`ChildProcessParseRunner` with a scripted `/bin/bash` child, so the pipes,
the scrubbed environment and the per-run working directory are the child's), shows loopback still reachable — the
positive control, so a failing attempt is the environment and not a broken script — and both a DNS-resolved and an
IP TCP attempt to port 443 failing; on a host with its own egress path the test **skips** with that reason instead of
passing vacuously. The test's parent-side check uses the IP connect as the only authoritative "this host has egress"
signal and prints the name lookup as evidence: a forwarding resolver can answer from outside the caller's network
namespace, so a successful lookup proves nothing about a route. Measured for exactly this reason: under
`--network none` `/etc/resolv.conf` still contains `nameserver 192.168.5.1` (the host's resolver) while
`getent hosts example.com` fails (`getent=2`), so the file's presence is not an egress path and a lookup result is
decided by the resolver's own network, not the child's.

### Contract gap (AC4, event half)

AC4's "and the attempt is logged as a security event" clause is a **contract gap**, not a code gap: the detection
site it would need — `ChildProcessParseRunner`, which is the only place that observes a failed or blocked attempt —
emits nothing today, and the V1 `security_event` type set (`docs/security/security-event-baseline.md`) is a closed
enumeration with no member for a blocked outbound attempt, while the table itself does not exist yet. This page
therefore records the gap rather than inventing a sixth type:
`11.5.1-T5.1` creates the table and the emitter, and `11.1.1-T4.1` owns the type set and the baseline revision; when
both exist, wiring the runner's failed attempt to that emitter is a small change in
`src/Modules/Evidgate.Modules.Evidence/Infrastructure/Parsing/Runner/`, with the reason code the runner already
computes (`parse_crashed`/`parse_timeout` distinguish an attempt from a resource kill only if the child reports it,
`[INFERENCE]`).

## 4. Claim summary

- **Met:** the child can form no outbound connection by construction (no `System.Net` type in the built assembly)
  and receives no proxy/resolver/networking configuration in its environment.
- **Met where the container denies egress:** an attempt from a process started exactly like the child fails (proven
  in a `--network none` container); this is the shape this repository can test, not the production shape.
- **Not met (deployment, 10.2.1-T1.1):** a runtime guarantee for the production Worker container.
- **Not met (11.5.1-T5.1 / 11.1.1-T4.1):** the security event for the attempt.
