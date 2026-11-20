# fixtures/variant-hash/

Golden vectors for the variant identity (04.3.2-T1.3, AC3): the normalised parameter set and its
`sha256:<hex>` digest. The same file is asserted by both runtimes, so a divergence in the
normaliser, the RFC 8785 canonicaliser or the digest fails one of them:

- `packages/adapter-playwright/src/variant.test.ts` — the reporter (`normalizeVariant`);
- `tests/Tenancy.IntegrationTests/VariantGoldenVectorTests.cs` — the server
  (`Evidgate.Modules.Catalog.Domain.VariantNormalizer` + `CanonicalHash`).

`key-sets.json` pins the enum-like and non-identifying key sets of both runtimes (exact, order-free):
`packages/variant/src/keySets.test.ts` reads the literals from `normalize.ts`, and
`tests/Tenancy.IntegrationTests/VariantKeySetPinTests.cs` reads the private sets of `ParameterSecretRule`.
Change a key set on one side and this file together.

## Provenance

Handcrafted, synthetic values only. Every `expect` value is what the server's
`VariantNormalizer.Normalize` produces for the `params` next to it: the reporter ports that method,
and the digest is `sha256:<lowercase hex>` over the RFC 8785 canonical form of the normalised
parameters (`canonicalize` 5.1.0 on the Node side, `Evidgate.BuildingBlocks.Canonicalization` on the
server side). The port is exact for the normalisation rules and the digest; the two places where it
cannot be — the `too_large` measure and a number's spelling — are under "Notes the vectors pin".

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
- **`refuse-too-large`** exceeds the 8000-byte parameter cap with ASCII content only, so every
  measure of the set agrees.
- **The `too_large` measure is the server's serialisation, not the canonical byte count.** The
  server measures `Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(result))` under the default
  `JavaScriptEncoder`, which writes every non-ASCII character, the backtick, and the ASCII
  characters `"`, `&`, `'`, `+`, `<`, `>` as a six-byte `\uXXXX` escape (an astral character as two
  escapes, twelve bytes) and `\` as `\\`. The reporter reproduces that serialisation, so
  `accept-near-limit-non-ascii` / `refuse-too-large-non-ascii` (U+00E9), `accept-near-limit-html` /
  `refuse-too-large-html` (the whole escaped ASCII set) and `accept-near-limit-astral` /
  `refuse-too-large-astral` (U+1F9EA) sit either side of the cap on that measure alone: their
  canonical JSON is under 3 KB, so a canonical-byte-count rule would accept all six.
  `refuse-too-large-number-notation` pins the number notation the same way — the server writes the
  `double` `1e-7` as `1E-07` (five bytes) where ECMAScript writes `1e-7` (four).
- **A number's spelling is not recoverable, so three spellings are deliberately absent.** The
  reporter is handed a JavaScript `number`, so `1e20` and `100000000000000000000` are one value,
  while the server branches on the raw JSON text (`VariantNormalizer.Scalar`). A value in
  [2^53, 1e21) written with an exponent or a fraction (`9.1e15`, `1e16`, `1e20`) is accepted by the
  server and refused by the reporter; a value of 1e21 or more written in full digits
  (`1000000000000000000000`) is refused by the server and accepted by the reporter; and `-0.0`
  against `-0` hashes the same but is one byte longer on the server. See the header of
  `packages/adapter-playwright/src/variant.ts`.
