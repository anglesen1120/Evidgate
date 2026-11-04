// The `runs create` QA specs' own extras: the astral and right-to-left-override fixtures and
// the refusal envelope. Every other seam they inject — the stub transports, the argvs, the
// constants and the per-test temp roots — comes from `runsCreate.testing.ts`, so the command
// has one fixture family rather than two near-copies.
//
// This file is not a spec: the shared Vitest base collects `src/**/*.test.ts` only. It is
// also excluded from the emitting project in tsconfig.json, so it never reaches `dist`; the
// no-emit tsconfig.spec.json is the project that type-checks it.
import { jsonResponse, useRunsCreateSpec } from './runsCreate.testing.js';

export {
  apiCalls,
  capture,
  createArgv,
  created,
  invoke,
  jsonResponse,
  manifestHash,
  manifestId,
  otherManifestHash,
  otherManifestId,
  runId,
  runsCreateArgv,
  sentBody,
  stubDevFetch,
  stubFetch,
} from './runsCreate.testing.js';

/** A single astral code point (two UTF-16 units) for the length-counting cases. */
export const astral = '\u{1F600}';
/** U+202E RIGHT-TO-LEFT OVERRIDE, a `Cf` character the escaper must neutralise. */
export const rlo = '\u202e';

/** A 00 §9.2 error envelope with the given code (and optional details). */
export function envelope(
  status: number,
  code: string,
  message: string,
  details?: unknown,
): Response {
  return jsonResponse(status, {
    error: {
      code,
      message,
      request_id: `req-${code}`,
      retryable: false,
      ...(details === undefined ? {} : { details }),
    },
  });
}

/** The environment factories the QA specs drive the command with, from the shared seam. */
export function useRunsCreateQaSpec() {
  const base = useRunsCreateSpec();
  return { devEnv: () => base.devEnv(), ingestEnv: () => base.ingestEnv() };
}
