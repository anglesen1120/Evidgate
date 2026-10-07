# fixtures/variant-hash/

Golden vectors for the variant identity (04.3.2-T1.3, AC3): the normalised parameter set and its
`sha256:<hex>` digest. The same file is asserted by both runtimes, so a divergence in the
normaliser, the RFC 8785 canonicaliser or the digest fails one of them:

- `packages/adapter-playwright/src/variant.test.ts` — the reporter (`normalizeVariant`);
- `tests/Tenancy.IntegrationTests/VariantGoldenVectorTests.cs` — the server
  (`Evidgate.Modules.Catalog.Domain.VariantNormalizer` + `CanonicalHash`).

## Provenance

Handcrafted, synthetic values only. Every `expect` value is what the server's
`VariantNormalizer.Normalize` produces for the `params` next to it: the reporter is a byte-for-byte
port of that method, and the digest is `sha256:<lowercase hex>` over the RFC 8785 canonical form of
the normalised parameters (`canonicalize` 5.1.0 on the Node side,
`Evidgate.BuildingBlocks.Canonicalization` on the server side).

## Format

A JSON array; one object per vector:

| field    | rule                                                                                             |
| -------- | ------------------------------------------------------------------------------------------------ |
| `name`   | unique vector name, used as the test case name on both runtimes                                  |
| `params` | the parameter object as sent (a JSON value; `null` means no parameters)                          |
| `expect` | `{ "hash": "sha256:…" }` when the set is accepted, else `{ "problems": [{ "path", "reason" }] }` |

`problems` is compared in order, as `(path, reason)` pairs.

## Notes the vectors pin

- **Key order, enum-like case/whitespace and non-identifying keys** collapse to one hash
  (`key-order-a`/`key-order-b`, `enum-like-plain`/`enum-like-case-whitespace`,
  `non-identifying-dropped`).
- **No Unicode normalisation.** The server does not apply NFC (or any other form), so
  `unicode-nfc-composed` (`café`, U+00E9) and `unicode-nfd-decomposed` (`café`, `e` + U+0301) are
  two different variants with two different hashes. 04.3.2 AC3's "code-point-normalised" wording is
  not what the merged server does; the reporter mirrors the server (see the task's open item).
- **`-0`** is spelled raw in `number-negative-zero`; both runtimes normalise it to `0`.
- **`absent-null`** covers absent parameters (`null`), which are the empty parameter set `{}`.
- **`refuse-too-large`** exceeds the 8000-byte parameter cap with ASCII content only, so the
  reporter's canonical byte count and the server's `JsonSerializer` byte count agree.
