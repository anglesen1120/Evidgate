# RFC 8785 reference vectors

`input/<name>.json` → `output/<name>.json` are the RFC 8785 (JCS) test vectors published
with [cyberphone/json-canonicalization](https://github.com/cyberphone/json-canonicalization/tree/master/testdata),
fetched on 2026-09-26 from
`https://raw.githubusercontent.com/cyberphone/json-canonicalization/master/testdata/{input,output}/<name>.json`.

Source licence: Apache-2.0, © 2018 Anders Rundgren (the vector repository's `LICENSE`);
the file is distributed here unmodified. `input/` holds the document to canonicalize and
`output/` the exact expected canonical byte sequence (no trailing newline).

These files are compared as **bytes**, so they must never be reformatted: the
`output/*.json` files are the canonical form itself (for example `[56,{"1":[],"10":null,"d":true}]`
on a single line), not pretty-printed JSON. `.prettierignore` excludes this directory.

Both suites assert them: `tests/Canonicalization.UnitTests/Rfc8785VectorTests.cs` (via
`JsonCanonicalizer`) and `packages/cli/src/canonical.test.ts` (via `canonicalize` 5.1.0),
so a regression in either runtime fails that runtime's suite. The directory sits under
`fixtures/` rather than in the test project because the CLI workspace cannot read a
project-local `TestData/`.

The number-serialization vectors of RFC 8785 Appendix B and §3.2.2/§3.2.3 are asserted
inline in `Rfc8785VectorTests` instead of being copied here. The 100-million-value
developer-portal file (`es6testfile100m.txt.gz`, ~1.8 GB) is deliberately not vendored.

Coverage beyond these files (run 2026-09-26 on this machine, nothing committed):

- A differential run against `canonicalize` 5.1.0 on Node 26.10.0 (V8 is the reference
  implementation RFC 8785 §3.2.2.3 names) over 27,146 generated documents — random
  documents with strings, escapes, unicode and nesting, the Appendix B number set, random
  IEEE 754 bit patterns, and adversarial cases. Every document that both runtimes accept
  and that the runtime parses consistently has the same canonical text and digest.
  An earlier version of this note claimed "37,046 cases, 0 mismatches"; that figure did not
  reproduce and is withdrawn. The repeat found 38 of the 24,174 both-accepted documents
  where the JS canonical text differed from .NET's, all traced to a Node 26.10.0
  `JSON.parse` defect: once a process is warm, a member name written with a control escape
  can be mis-decoded, so the CLI's canonical text can depend on process history. .NET was
  RFC-correct in every one of those cases, and the CLI wrapper now withholds a digest when
  the runtime cannot reproduce its own canonical text (`assertCanonicalRoundTrip` in
  `packages/cli/src/canonical.ts`). The reproduction harness and the measured rates are
  recorded in the feature plan §9 notes.
