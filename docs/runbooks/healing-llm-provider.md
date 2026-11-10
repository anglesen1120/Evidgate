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
| `http` | no base address, a relative one, plain HTTP outside Development, a base that does not end in `/`, or one carrying a query or fragment | `EVIDGATE__HEALING__LLM__BASEADDRESS` |
| `http` | no key name, or a name `SecretNames` does not declare | `EVIDGATE__HEALING__LLM__APIKEYSECRET` |

`BASEADDRESS` must end with `/` because the adapter posts a relative path under it, and it may not point at a redirect: the client does not follow one, so a provider behind a redirect fails its calls as `error` instead of leaving the configured endpoint. Plain `http` is accepted in Development only, because the key travels in an `Authorization` header.

## The provider key

The key itself is never configuration. `APIKEYSECRET` holds a name from the `SecretNames` registry — for the generic provider, `EVIDGATE__HEALING__LLM__APIKEY` — and the adapter's request handler resolves that name through `ISecretProvider` on **every** call:

- the options member that spells the name has an internal setter, so the binder never reads a value into it, and it stays null (a test asserts this);
- nothing caches the key, so a rotated value is used by the next call;
- the key reaches the provider in the `Authorization` header and nowhere else: not the request URI, not a log line, not an exception message, not the result the adapter returns;
- a name that resolves to nothing sends no request at all and returns an `error` result.

The key's store, owner and rotation cadence are in `infra/secrets-inventory.json`. To rotate it, write the new value under the same secret name in the deployment's secret store; no restart is needed, because the handler reads it per call. A missing or unusable key is a boot failure naming `EVIDGATE__HEALING__LLM__APIKEYSECRET`, never the value.

## What the gateway does with a disabled provider

The provider selection is one guard of several. Before any provider call the gateway also reads the effective healing switch for the project and the period budget, and refuses with a typed reason (`provider_disabled`, `healing_disabled`, `budget_exceeded`, `budget_not_provisioned`) when one of them says no. A refusal records no call row, spends nothing and writes one telemetry counter tagged with the reason. `shadow` and `enabled` both proceed: shadow healing still analyses, it just applies nothing. The switch itself is described in [kill-switch.md](kill-switch.md), and the V1 clamp that keeps `enabled` unreachable is described there too.

## Check

1. Start a host pinned to Production with none of the three keys set: it must exit before listening, and the message must name `EVIDGATE__HEALING__LLM__PROVIDER`.
2. Start it with `EVIDGATE__HEALING__LLM__PROVIDER=none`: it must start, and a healing call must return the `provider_disabled` refusal with no request leaving the process and no `healing_llm_call` row.
3. Start it with `EVIDGATE__HEALING__LLM__PROVIDER=mock`: it must refuse the boot, naming the same key (and Development must accept it).
4. Start it with `http` and a base address only: the message must name `EVIDGATE__HEALING__LLM__APIKEYSECRET`. Add a name that resolves, then call: the provider must receive one `Authorization: Bearer …` request, and no log line may carry the key.

`tests/Hosts.IntegrationTests/OptionsValidationTests.HealingProvider.cs` boots rows 1 to 4 as real processes, `HealingProviderSelectionTests` proves the mock cannot be resolved outside Development, and `HealingLlmApiKeyHandlerTests` proves the key is presented and never logged.

## Operator note

Every non-Development Api and Worker start now states a provider. Use `none` unless the deployment runs the real provider; the image health check (`scripts/image-db-checks.sh`) and the observability smoke (`scripts/dev/observability-smoke.sh`) set `none` for their throwaway hosts. Add the key to every staging and prod deployment before rolling out this version, or the host will refuse to start.
