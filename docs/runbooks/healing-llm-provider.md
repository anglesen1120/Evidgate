# The healing LLM provider

The healing gateway places model calls through one **provider adapter**, and which adapter runs is deployment configuration. A host that names a provider it may not run refuses to start, so no deployment reaches a provider by silence and none reaches the offline mock by accident.

## Configuration

| Key | Meaning |
|---|---|
| `EVIDGATE__HEALING__LLM__PROVIDER` | `none`, `mock` or `http` (case-insensitive) |
| `EVIDGATE__HEALING__LLM__BASEADDRESS` | Absolute base address of the provider's API, required for `http` |
| `EVIDGATE__HEALING__LLM__APIKEYSECRET` | **Name** of the secret holding the provider key, required for `http` |

- `none` places no call: the disabled adapter answers with an `error` result and the gateway refuses before it is reached. It is the deliberate value for a deployment that runs no healing.
- `mock` is the deterministic offline adapter. It is registered **only in Development**, so a production container has none to resolve.
- `http` is the real provider. It needs a base address and a key name, and a boot without either is refused.
- Unset means `mock` in Development and nothing anywhere else, where it is a boot failure.

Start-up refusals name the key and never the configured value:

| Selected | Refused when | Message names |
|---|---|---|
| unset | outside Development | `EVIDGATE__HEALING__LLM__PROVIDER` |
| `mock` | outside Development | `EVIDGATE__HEALING__LLM__PROVIDER` |
| anything else | not one of the three words | `EVIDGATE__HEALING__LLM__PROVIDER` |
| `http` | no base address, a relative one, plain HTTP outside Development, a base that does not end in `/`, or one carrying a query, a fragment or user information | `EVIDGATE__HEALING__LLM__BASEADDRESS` |
| `http` | no key name, or one other than `EVIDGATE__HEALING__LLM__APIKEY` | `EVIDGATE__HEALING__LLM__APIKEYSECRET` |

Outside Development `BASEADDRESS` must also name a public host: a loopback, private, link-local or unique-local IP literal (cloud metadata included), `localhost` and unqualified or `.local`/`.internal` names are refused at boot. At run time the adapter's egress handler refuses any request whose scheme, host, port or path prefix differs from `BASEADDRESS` before a key is resolved for it, and every request carries `max_tokens` equal to the output ceiling held in the budget. Name resolution is not judged: a public name that resolves to an internal address is not stopped here.

`BASEADDRESS` must end with `/` because the adapter posts a relative path under it, and it may not point at a redirect: the client does not follow one, so a provider behind a redirect fails its calls as `error` instead of leaving the configured endpoint. It may not carry user information either, because credentials in the URL would travel on every request and surface in a proxy trace; the key belongs in the secret the handler resolves. Plain `http` is accepted in Development only, because the key travels in an `Authorization` header.

## The provider key

The key itself is never configuration. `APIKEYSECRET` holds the name of the provider key's secret — `EVIDGATE__HEALING__LLM__APIKEY`, the one name start-up accepts — and the adapter's request handler resolves that name through `ISecretProvider` on **every** call:

- the options member that spells the name has an internal setter, so the binder never reads a value into it, and it stays null (a test asserts this);
- nothing caches the key, so a rotated value is used by the next call;
- the key reaches the provider in the `Authorization` header and nowhere else: not the request URI, not a log line, not an exception message, not the result the adapter returns;
- a name that resolves to nothing, or a value carrying CR, LF or NUL, sends no request at all and returns an `error` result.

The key's store, owner and rotation cadence are in `infra/secrets-inventory.json`. To rotate it, write the new value under the same secret name in the deployment's secret store; no restart is needed, because the handler reads it per call.

Start-up judges the name and reads no value: an empty name, or one other than `EVIDGATE__HEALING__LLM__APIKEY`, fails the boot naming `EVIDGATE__HEALING__LLM__APIKEYSECRET` and never the value. A name whose value is missing at call time sends no request: the call fails as an `error` attempt row, so a deployment may start before its secret store holds the key, and heals once it does.

## What the gateway does with a disabled provider

The provider selection is one guard of several. Before any provider call the gateway also reads the effective healing switch for the project and the period budget, and refuses with a typed reason (`provider_disabled`, `healing_disabled`, `budget_exceeded`, `budget_not_provisioned`, `budget_unreadable`) when one of them says no; only the offline adapter is exempt from needing a budget row, because it spends nothing. A refusal records no call row, spends nothing and writes one telemetry counter tagged with the reason. `shadow` and `enabled` both proceed: shadow healing still analyses, it just applies nothing. The switch itself is described in [kill-switch.md](kill-switch.md), and the V1 clamp that keeps `enabled` unreachable is described there too.

## Limits before a real endpoint

Three limits are open, and a deployment registering a real provider must plan around them:

- The budget guard is **advisory**. The gateway reads the period's spend row and then places the call, and nothing reserves the tokens in between, so two calls running together can both pass a row with room for one. Treat the caps as a soft limit and set them well under the money that may be spent; the consume path that reserves against the row is 08.5.1-T1.4's.
- The response body is read after the resilience pipeline returns and is not yet bounded by an attempt timeout (carried row `AI gateway delta lows (S145)`), so a slow-drip body under the 1 MiB cap is bounded only by the caller's token. Do not register a real endpoint until that row is closed; `http` may point at a stub the deployment controls.
- The provider call still runs inside the caller's tenant transaction, so a caller rollback discards its attempt rows with the rest of that unit of work. 08.5.1-T1.4 owns the two-phase shape — reserve, call outside the transaction, commit — that separates the two.

## Check

1. Start a host pinned to Production with none of the three keys set: it must exit before listening, and the message must name `EVIDGATE__HEALING__LLM__PROVIDER`.
2. Start it with `EVIDGATE__HEALING__LLM__PROVIDER=none`: it must start, and a healing call must return the `provider_disabled` refusal with no request leaving the process and no `healing_llm_call` row.
3. Start it with `EVIDGATE__HEALING__LLM__PROVIDER=mock`: it must refuse the boot, naming the same key (and Development must accept it).
4. Start it with `http` and a base address only: the message must name `EVIDGATE__HEALING__LLM__APIKEYSECRET`. Add a value under that name, then call: the provider must receive one `Authorization: Bearer …` request, and no log line may carry the key.

`tests/Hosts.IntegrationTests/OptionsValidationTests.HealingProvider.cs` holds the whole start-up story in two forms: `Production_Without_A_Provider_Fails_Startup_Naming_The_Key` starts the real Api as a subprocess with the healing keys removed (row 1), and `A_Provider_Selection_Is_Validated_At_Boot` composes the module in a `ServiceCollection` and reads `IOptions<HealingProviderOptions>` for every other selection and refusal, rows 2 to 4 included — the only form that can see a refusal the host's own exit path already reports. `HealingProviderSelectionTests` proves the mock cannot be resolved outside Development, and `HealingLlmApiKeyHandlerTests` proves the key is presented and never logged. The HTTP adapter's transport behaviour — a redirect, a non-success status, the retry and time budget — is covered in `HealingLlmGatewayTests` over a stub `HttpMessageHandler`, with no socket.

## Operator note

Every non-Development Api and Worker start now states a provider. Use `none` unless the deployment runs the real provider; the image health check (`scripts/image-db-checks.sh`) and the observability smoke (`scripts/dev/observability-smoke.sh`) set `none` for their throwaway hosts. Add the key to every staging and prod deployment before rolling out this version: the host starts without it, but every healing call then fails as an `error` attempt with nothing sent.
