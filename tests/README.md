# tests/

Test projects (00 §5.2): `<Project>.UnitTests`, `<Module>.IntegrationTests` (Testcontainers), `Architecture.Tests`, `Contract.Tests`, `Isolation.Tests`. Test projects are created by 01.2.1 (unit/architecture conventions) and 01.6.1 (integration harness), not by the skeleton story.

## Naming convention (01.6.1-T1.1)

| Suffix                      | Meaning                                                                                                                                                                                                                                                  | Docker | Example                      |
| --------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- | ------ | ---------------------------- |
| `<Project>.UnitTests`       | Pure in-process logic. No network, no container, no database. Gets the no-network guard (below).                                                                                                                                                         | no     | `Canonicalization.UnitTests` |
| `<Module>.IntegrationTests` | Real PostgreSQL/Kestrel/S3 through Testcontainers; every Docker-backed test _class_ carries `[Trait("Category","Docker")]` (in-process classes in the same assembly do not, so `EVIDGATE_VERIFY_SKIP_DOCKER=1` skips exactly the container-backed ones). | yes    | `Migrator.IntegrationTests`  |
| `Architecture.Tests`        | Reflection/layout rules over the `Evidgate.*` assemblies (01.2.1).                                                                                                                                                                                       | no     | `Architecture.Tests`         |

Every project under `tests/` deliberately avoids the `Evidgate.` prefix (the `<Project>`/`<Module>` part is free), so the `Architecture.Tests` reflection scan (`Evidgate.*.dll` in the output directory) never analyses a test assembly.

## Shared build conventions — `tests/Directory.Build.props`

Imported automatically by every project under `tests/`. It imports the repository-root `Directory.Build.props` first, then adds what every test project shares:

- `<OutputType>Exe</OutputType>` (`global.json` pins `test.runner` to Microsoft.Testing.Platform).
- `Microsoft.NET.Test.Sdk`, `xunit.v3`, `xunit.runner.visualstudio`, `Shouldly`, `NSubstitute`.
- The no-network guard source file for projects whose name ends in `.UnitTests` (matched case-insensitively).

Container, ASP.NET, storage and driver packages stay in the individual csproj files. A project must not re-add the shared references.

## No-network default for unit projects (01.6.1-T1.1, 00 §5.6 W9)

`tests/Shared/NoNetworkGuard.cs` is compiled into every `*.UnitTests` assembly (the props inject it; the file also carries `[assembly: AssemblyFixture]`), so it arms before the first test runs:

- The process default proxy (`HttpClient.DefaultProxy`) is set to a black hole on the reserved loopback port 1, and that proxy bypasses nothing — `IsBypassed` is false for a normal host _and_ for `localhost`/`127.0.0.1` (measured, 01.6.1-T1.1). A `HttpClient` on its default handler therefore fails with `HttpRequestException` (`Connection refused (127.0.0.1:1)`) instead of reaching the network or a loopback test server.
- `HTTP_PROXY`/`HTTPS_PROXY`/`ALL_PROXY` (upper and lower case) are set to the same black hole and `NO_PROXY` is emptied, so a child process that builds its proxy from the environment (Node CLI, curl, git) cannot reach the network either.

It does **not** catch raw `Socket`/`Dns` calls or ADO.NET/Npgsql connections: no managed hook intercepts those, so they remain forbidden by review. Suites that genuinely need sockets are `*.IntegrationTests` and do not get the file.

Each unit assembly also runs `NoNetworkConventionTests`, three I/O-free checks that fail as soon as the assembly fixture stops running (verified by commenting the attribute out): `Guard_Is_Armed` reads the AppContext flag, `Http_Requests_Route_To_The_Blackhole` asserts what `HttpClient.DefaultProxy` answers for a normal host and for loopback, and `Environment_Proxies_Route_To_The_Blackhole` asserts the `*_PROXY`/`NO_PROXY` variables the guard pins. A deliberate network call to a real host is blocked with `HttpRequestException` while the guard is armed, and succeeds without it. What the guard still does not stop: a raw `Socket`/`Dns` call, an ADO.NET/Npgsql connection, or an `HttpClient` built with an explicit `UseProxy = false` handler — those stay review-only.
