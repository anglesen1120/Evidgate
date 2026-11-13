# fixtures/execution-key/

Golden vectors for `execution_key`, asserted by both runtimes from this one file:

- `packages/adapter-playwright/src/executionKey.test.ts` — the reporter (`computeExecutionKey`);
- `tests/Tenancy.IntegrationTests/ExecutionKeyTests.cs` — the server (`ExecutionKey.Compute`).

Layout (defined once in `src/Modules/Evidgate.Modules.Orchestrator/Domain/ExecutionKey.cs`): lowercase-hex
SHA-256 over three fields, each a 4-byte big-endian UTF-8 byte length followed by its UTF-8 bytes, in the
order `case_id` (lowercase `D` form), `variant_hash` (`sha256:<hex>`), `target_env`.

Each vector: `name`, `case_id`, `variant_hash`, `target_env`, `expect` (64 lowercase hex). Handcrafted,
synthetic values only. The two `boundary_*` vectors pin that the length prefix keeps `("a","bc")` and
`("ab","c")` apart; `non_ascii_env` pins UTF-8 byte (not UTF-16 unit) lengths.
