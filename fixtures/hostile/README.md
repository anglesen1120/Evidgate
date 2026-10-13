# fixtures/hostile/ — the hostile-input corpus (06.3.2)

One committed file per guard of the 06.3.1 parser profile, plus `manifest.json` as the corpus's machine-readable
index. `tests/Tenancy.IntegrationTests/HostileFixtureTests.cs` drives **every** row through the entry point the row
names and asserts the documented failure code; `HostileFixtureMutationSmokeTests.cs` proves the guards are
load-bearing by running the archive rows against one deliberately weakened cap. The `fixtures-hostile` CI job (and
`scripts/verify.sh --stage fixtures-hostile`) runs both classes; no Docker is needed. The same corpus seeds the
nightly fuzz harness (06.3.2-T2, below), which promotes any crash it finds back into this directory.

## Layout

This is **not** a `<domain>/<name>/` FixtureLoader corpus (see `fixtures/README.md`): a fixture directory in that
layout holds exactly one `input.*`, and both loaders refuse any directory without `meta.json`. The corpus is flat
and indexed by `manifest.json` instead — the same exemption `fixtures/ci-recipes/`, `fixtures/contracts/` and
`fixtures/threat-model/` take. Every committed file is a few KiB; the two fixtures whose size or count is the
attack (`zipbomb.zip`, `oversized.bin`) are **generated at test time** by `GenerateBombFixture` /
`GenerateOversizedFixture` into a temp directory, so no binary of theirs is committed (AC1).

## `manifest.json`

A JSON array; one object per fixture, with only these keys:

| key                     | rule                                                                                                                       |
| ----------------------- | -------------------------------------------------------------------------------------------------------------------------- |
| `file`                  | the file name inside this directory (or the generated one's name)                                                          |
| `guard`                 | `<guard-id> — <sentence>`; the id is the first word and must be one of the closed guard list below                         |
| `driven_through`        | `IParsing.ParseXmlReport`, `IParsing.EnumerateArchive`, `ArtifactCompletionVerifier (complete path)` or the 06.2.3 scanner |
| `expected_failure_code` | the code the corpus must observe (exactly one of this and `expected_outcome`)                                              |
| `expected_outcome`      | `accepted` (the positive control) or `referenced` (another story owns the assertion)                                       |
| `declared_media_type`   | the media type a complete-path row declares; absent for the parse-driven rows                                              |
| `generated`             | `true` for a fixture built at test time; absent otherwise                                                                  |

The test asserts the row count equals the committed file count plus the generated count (AC1), that every row's
guard id and failure code exist, and that no `xxe*.xml` / `billion_laughs*` copy lives anywhere else in the
repository (AC5).

## Guards

| guard id                | decision point (06.3.1)                                                                                                               | tested by                                                                    |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------------------------------------- |
| `xml-dtd`               | `SafeXml`'s prologue scan refuses `<!DOCTYPE`/`<!ENTITY`                                                                              | `xxe.xml`, `billion_laughs.xml`                                              |
| `xml-wellformed`        | the reader profile's DTD prohibition and well-formedness (a declared DTD in a wide encoding, a declaration carrying a non-ASCII byte) | `utf16_dtd.xml`, `utf32_dtd.xml`, `fuzz_5b9ba68261b8.xml`                    |
| `zip-entry-name`        | `SafeZipArchive`'s entry-name normalization (traversal, ZWJ, trailing dot)                                                            | `traversal.zip`, `zip_zwj_name.zip`, `zip_trailing_dot_name.zip`             |
| `zip-central-directory` | the end-of-central-directory pre-check (a fake record inside the comment, trailing bytes, a truncated archive)                        | `zip_fake_eocd_comment.zip`, `zip_trailing_bytes.zip`, `truncated_trace.zip` |
| `zip-entry-count`       | the declared entry count against `ParsingLimits.MaxZipEntries`                                                                        | `zipbomb.zip` (generated)                                                    |
| `complete-media-sniff`  | `MediaSniffer.HasSignature` / the sniff's fail-closed arm at `complete`                                                               | `media_*.{txt,bin,pdf,webm,webp,svg}`                                        |
| `complete-size-cap`     | the kind policy's `max_bytes` against the object the store reports                                                                    | `oversized.bin` (generated)                                                  |
| `secret-scan`           | 06.2.3's masking scanner (the assertion lives in that story)                                                                          | `planted_secret.txt`                                                         |

`zip_long_comment.zip` is the positive control: a long but well-formed comment closes exactly at the object's end,
so the profile must ACCEPT it — a corpus that only ever expects refusals cannot tell a guard from a blanket
refusal. `planted_secret.txt` carries a synthetic, self-describing value
(`EVIDGATE-DUMMY-NOT-A-SECRET-…`); `scripts/check-no-secret-literals.sh` treats that prefix as a documented
non-secret (see `scripts/no-secret-literals-allow.txt`).

## Fuzzing and promotion (06.3.2-T2)

This corpus is also the seed set of the 06.3.2-T2.1 fuzz harness (`HostileFuzzHarness.cs`, classes
`HostileFuzzTests` / `FuzzPromotionTests`, `[Trait("Category","Fuzz")]`, run by `scripts/verify.sh --stage
fixtures-fuzz` and the nightly `fixtures-fuzz` CI job on a 10-minute budget). It mutates these bytes with a
deterministic PRNG — a recorded seed (`EVIDGATE_FUZZ_SEED`, default `20261013`) replays a run exactly — and drives
every input through `IParsing.ParseXmlReport` and `IParsing.EnumerateArchive`, allowing only the documented
outcomes (`artifact_invalid`/422 with a closed `ParsingFailureReason`, or `payload_too_large`/413 for
`size_exceeded`). An escaped exception, an abandoned input or an undocumented reason is a finding: the bytes go
to the findings directory (`artifacts/fuzz-findings` in CI, uploaded on failure) and the run fails.

`[DECISION]` 06.3.2-T2.1 uses that seeded mutation fuzzer rather than `SharpFuzz`. SharpFuzz is coverage-guided
only with the AFL/libFuzzer native tooling and an IL-instrumented copy of the parser assembly in the CI image, and
a new dependency needs a licence-scan pass and an ADR-0002 row (00 §5.7 S6); `scripts/licence-policy.json` and
`docs/adr/0002-compatibility-and-licence-matrix.md` carry no SharpFuzz entry (grepped: zero hits), so the story's
"or a seeded mutation fuzzer if the licence check fails" arm applies and the run adds no dependency.

A finding can be promoted back into this directory: `FuzzPromotion.Promote` writes the bytes as a new committed
fixture and appends its `manifest.json` row, so the crash becomes a permanent case `HostileFixtureTests` asserts.
Promotion is refused unless the explicit opt-in is passed and `EVIDGATE_FUZZ_PROMOTE=1` is set — no CI job sets
it — so a CI run never writes here. The promoted row's guard id is the finding's entry point's owning guard
(`xml-wellformed` or `zip-central-directory`) and its `expected_failure_code` starts at the conservative
`malformed`; the operator reviewing a real crash tightens the row to the reason the fixed profile returns.

One promotion exists so far: `fuzz_5b9ba68261b8.xml`, found by the first smoke run (2026-10-13, seed `20261013`).
A non-ASCII byte inside the XML declaration made `System.Xml` throw `ArgumentOutOfRangeException` out of
`XmlTextReaderImpl.SwitchEncoding` instead of `XmlException`, so the profile answered with a crash rather than a
refusal; `SafeXml.Parse` now maps that exception to `malformed`, which is the code the row asserts.

## Provenance

`synthetic` for every file (00 §5.7 S3): the XML/zip/PDF/WebM/WebP bytes are handcrafted or generated by
`GenerateBombFixture`/`GenerateOversizedFixture` from a fixed seed (`20261013`), never taken from a real report,
trace or credential. The committed archives were written deterministically (fixed DOS date, no clock, no random
source); the generated ones are pure functions of the seed, so a rebuild is byte-identical.
