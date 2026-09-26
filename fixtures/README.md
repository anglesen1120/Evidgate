# fixtures/

Golden reports and expected normalised output per producer (00 §5.2). Populated by the ingestion adapter stories in plan 04 and used by contract/normalisation tests.

Two directories hold byte-exact vectors asserted by more than one runtime, which is why they live here rather than inside a single test project:

- `canonical/` — the Evidgate golden documents (manifest, policy, candidate, evidence) with their canonical text and `sha256:<hex>` digest inline (01.3.2-T4.2, AC5);
- `rfc8785/` — the RFC 8785 reference input/output pairs published with cyberphone/json-canonicalization (Apache-2.0, see its README) (01.3.2-T4.1, AC5). Both `tests/Canonicalization.UnitTests` and `packages/cli/src/canonical.test.ts` read them; `.prettierignore` keeps the pairs byte-exact.
