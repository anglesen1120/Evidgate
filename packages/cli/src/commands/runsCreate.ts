// 04.2.2-T2.1: `evidgate runs create` — opens a run for an already-uploaded manifest
// with `POST /v1/projects/{p}/runs`, the `Idempotency-Key` header the route requires and
// the `run_id`/`manifest_hash`/`state` output (04.2.2 §2 AC2–AC5, §5, §9; contracts/cli.md
// §9). Every flag is validated BEFORE `getToken()`, so a bad flag makes no request at all
// — not even the token exchange (the `manifests upload` rule, 04.2.2 §8). Each option is
// single-use: every option is `multiple: true` so a repetition is refused as `usage`
// instead of silently taking the last value (§9 step 1). Lengths count code points
// (`--release` 1..128, the `--ci-ref` name 1..255), `refs/pull/<N>/merge` holds `N` to a
// safe integer ≥ 1, and the 201 `state` must match `^[a-z][a-z0-9_]{0,31}$` before
// anything is printed (§9 steps 1 and 3).
//
// The request never carries a policy, suite or environment: the server pins those from
// the manifest and the project's active policy, so a client-selected override has no
// owning story (D-S47-Q1, 2026-10-14). It also never reads `GITHUB_*` (or any other CI
// variable) to build the identity block; a `--ci-ref` the operator passes is the only
// source, and the server marks such a run `unverified_ref = true` (05.2.2-T1).
//
// A 401 purges the cached CI token (04.2.1-T1 hand-over, contracts/cli.md §7/§9); a 403
// or a 409 keeps it, because neither says the token is dead.
//
// The request is replay-safe: it carries the derived `Idempotency-Key`, so a network
// failure, a 429 or a 5xx is retried a bounded number of times by `callApi` with the same
// key and body, and every other 4xx is final.
import { createHash } from 'node:crypto';

import { createRun, type CreateRunRequest, type RunRefRequest } from '@evidgate/sdk';

import { getToken } from '../auth/getToken.js';
import { deleteCachedToken } from '../auth/tokenCache.js';
import { callApi } from '../callApi.js';
import { createApiClient } from '../client.js';
import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { serverErrorFromResponse, transportError } from '../serverError.js';

import type { CommandDefinition, ParsedValues } from './index.js';

/**
 * The usage-error hint `main.ts` attaches to its parse-level usage errors (`helpHint`
 * there, module-private). Repeated here so the command-level flag usage errors carry the
 * same pointer (contracts/cli.md §5 `usage` row); keep the two literals in sync.
 */
const helpHint = "run 'evidgate --help' for usage";
/** `manifest_id` is a UUID (openapi `CreateRunRequest.manifest_id`). */
const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
/** The server's `manifest_hash` form: `sha256:` + 64 lowercase hex (00 §7, RFC 8785). */
const manifestHashPattern = /^sha256:[0-9a-f]{64}$/;
/** openapi `IdempotencyKey`: ≤ 128 chars (00 §7/§9.1). */
const maxIdempotencyKeyLength = 128;
/** A client-supplied key takes exactly the printable-ASCII range: no space, no control. */
const idempotencyKeyPattern = new RegExp(`^[\\x21-\\x7E]{1,${maxIdempotencyKeyLength}}$`);
/**
 * `RunRefRequest.name`: 1..255 code points — a branch/tag name, or `<N>/merge` for a PR
 * (openapi `RunRefRequest`; contracts/cli.md §9).
 */
const maxRefNameLength = 255;
/** `CreateRunRequest.release`: 1..128 code points (openapi `CreateRunRequest.release`). */
const maxReleaseLength = 128;
/**
 * The derived-key namespace. Versioned and part of the pre-image, so a later change to
 * the derivation cannot silently reuse a stored key (AC4).
 */
const runKeyVersion = 'evidgate.run-key.v1';
/**
 * `refs/pull/<N>/merge` — the GITHUB_REF form for a pull request. `N` is its decimal
 * digits with no leading zero; the reader below additionally requires a safe integer ≥ 1.
 */
const pullRequestRefPattern = /^refs\/pull\/([1-9][0-9]*)\/merge$/;
/**
 * `RunCreated.state` (openapi, `00 §9.3`): lowercase snake_case, 1..32 characters. No
 * `m` flag, so `$` anchors at the very end of the input and a newline or an ANSI escape
 * inside the value cannot pass.
 */
const statePattern = /^[a-z][a-z0-9_]{0,31}$/;

/** Inputs of the AC4 derived key: exactly the values a `runs create` request is built from. */
export interface RunIdempotencyKeyInput {
  /** The `--manifest-id` value. */
  readonly manifestId: string;
  /** The `--manifest-hash` value. */
  readonly manifestHash: string;
  /**
   * The trimmed `--release`, or `null`/absent when the flag is absent. Absent and an
   * explicit `null` are the same pre-image slot, never an omitted or empty one.
   */
  readonly release?: string | null | undefined;
  /** The raw `--ci-ref`, or `null`/absent when the flag is absent (same null rule). */
  readonly ciRef?: string | null | undefined;
}

/**
 * The AC4 derived `Idempotency-Key` (amended 2026-10-14, D-S47-Q1): the lowercase hex
 * SHA-256 of the UTF-8 JSON array `['evidgate.run-key.v1', manifest_hash, manifest_id,
 * release ?? null, ci_ref ?? null]`.
 *
 * A JSON array is unambiguous (no separator can collide with a value) and the field order
 * is fixed, so two invocations with the same inputs — in the same process or across
 * process restarts — always derive the same key, and a request body that differs in any
 * of these values never reuses it. Every value the CLI can put in the body feeds the key.
 * The result is 64 hex characters, well inside the 128-char route limit. A key that
 * feeds no secret is not sensitive; it is still never printed.
 */
export function deriveRunIdempotencyKey(input: RunIdempotencyKeyInput): string {
  const preImage = JSON.stringify([
    runKeyVersion,
    input.manifestHash,
    input.manifestId,
    input.release ?? null,
    input.ciRef ?? null,
  ]);
  return createHash('sha256').update(preImage, 'utf8').digest('hex');
}

export const runsCreateCommand: CommandDefinition = {
  name: 'runs create',
  summary: 'create a run for an uploaded manifest',
  requireToken: true,
  options: {
    // `multiple: true` is what makes a repetition observable: with the default `false`
    // `parseArgs` keeps the last value and reports nothing, so `--idempotency-key a
    // --idempotency-key b` would quietly send `b`. Every value therefore arrives as an
    // array and the readers below refuse more than one entry (contracts/cli.md §9 step 1).
    'manifest-id': { type: 'string', multiple: true },
    'manifest-hash': { type: 'string', multiple: true },
    release: { type: 'string', multiple: true },
    'ci-ref': { type: 'string', multiple: true },
    'idempotency-key': { type: 'string', multiple: true },
  },
  run: async ({ config, io, values, deps }) => {
    // Flags first, token second: an invalid flag must not reach the network, and a
    // repeated flag (an array of two or more entries from `parseArgs`) is refused
    // rather than silently taking the last value.
    const manifestId = requiredFlag(values, 'manifest-id');
    if (!uuidPattern.test(manifestId)) {
      throw usageError('--manifest-id must be a UUID');
    }
    const manifestHash = requiredFlag(values, 'manifest-hash');
    if (!manifestHashPattern.test(manifestHash)) {
      throw usageError('--manifest-hash must be sha256:<64 lowercase hex>');
    }
    const rawRelease = flagValue(values, 'release');
    const release = rawRelease === undefined ? undefined : rawRelease.trim();
    if (
      release !== undefined &&
      (release.length === 0 || codePointLength(release) > maxReleaseLength)
    ) {
      throw usageError(`--release must be 1..${maxReleaseLength} characters`);
    }
    const ciRef = flagValue(values, 'ci-ref');
    const ref = ciRef === undefined ? undefined : parseCiRef(ciRef);
    const rawKey = flagValue(values, 'idempotency-key');
    if (rawKey !== undefined && !idempotencyKeyPattern.test(rawKey)) {
      throw usageError(
        `--idempotency-key must be 1..${maxIdempotencyKeyLength} printable ASCII characters`,
      );
    }
    const idempotencyKey =
      rawKey ?? deriveRunIdempotencyKey({ manifestId, manifestHash, release, ciRef });

    const { token } = await getToken(config, deps);
    const client = createApiClient(config, token, deps.fetch ?? fetch);
    const body: CreateRunRequest = {
      manifest_id: manifestId,
      ...(release === undefined ? {} : { release }),
      ...(ref === undefined ? {} : { ref }),
    };
    const result = await callApi(
      (signal) =>
        createRun({
          client,
          path: { p: config.project },
          headers: { 'Idempotency-Key': idempotencyKey },
          body,
          signal,
        }),
      // The request carries an Idempotency-Key, so a transport failure, a 429 or a 5xx is
      // retried with the SAME key and the SAME body: the server replays the stored run.
      { policy: 'replay-safe', deps },
    );
    const response = result.response;
    if (response === undefined) {
      // A network error, a timeout or a refused redirect: the SDK returns no response.
      throw transportError(result.error);
    }
    if (!response.ok) {
      if (response.status === 401) {
        // Best-effort: the server's error is the one that must surface (04.2.1 §6).
        await deleteCachedToken(config).catch(() => undefined);
      }
      // A 409 `idempotency_conflict` (same key, different body, T11) therefore reaches
      // stderr as `evidgate: error idempotency_conflict` and exits 3.
      throw serverErrorFromResponse(result.error, response, {
        code: 'request_failed',
        message: `the run create failed (HTTP ${response.status})`,
      });
    }
    const created = readCreated(result.data);
    if (config.json) {
      io.stdout(
        `${JSON.stringify({
          run_id: created.runId,
          manifest_hash: manifestHash,
          state: created.state,
        })}\n`,
      );
    } else {
      io.stdout(`run_id=${created.runId} manifest_hash=${manifestHash} state=${created.state}\n`);
    }
    return ExitCode.Ok;
  },
};

function usageError(message: string): CliError {
  return new CliError(message, { code: 'usage', hint: helpHint });
}

/**
 * Counts code points — the unit openapi `minLength`/`maxLength` and the server's
 * `char_length` use. `String.length` counts UTF-16 units, so one astral character would
 * count twice and a value the schema accepts would be refused (contracts/cli.md §9).
 */
function codePointLength(value: string): number {
  return [...value].length;
}

/**
 * The single value of one option, or `undefined` when it was absent. Every option is
 * declared `multiple: true`, so `parseArgs` reports a repetition as an array instead of
 * silently keeping the last value; two or more entries (or a non-string entry) are a
 * `usage` error that names the flag and never its value.
 */
function flagValue(values: ParsedValues, flag: string): string | undefined {
  const value = values[flag];
  if (value === undefined) {
    return undefined;
  }
  if (typeof value === 'string') {
    return value;
  }
  if (!Array.isArray(value)) {
    throw usageError(`--${flag} must be a single value`);
  }
  if (value.length !== 1) {
    throw usageError(`--${flag} may be given only once`);
  }
  const [only] = value;
  if (typeof only !== 'string') {
    throw usageError(`--${flag} must be a single value`);
  }
  return only;
}

/** A required string option; absent is a `usage` error, a repetition is refused. */
function requiredFlag(values: ParsedValues, flag: string): string {
  const value = flagValue(values, flag);
  if (value === undefined) {
    throw usageError(`missing required flag --${flag}`);
  }
  if (value.length === 0) {
    throw usageError(`--${flag} must be a single, non-empty value`);
  }
  return value;
}

/**
 * Maps `--ci-ref` to the only `ref` member the CLI ever sends (05.2.2-T1). The value
 * must be a full ref: `refs/heads/<name>` (branch), `refs/tags/<name>` (tag) or
 * `refs/pull/<N>/merge` (pull request, `N` a safe integer ≥ 1, so ≤ 9007199254740991).
 * Anything else — an unprefixed branch name, an empty ref, another `refs/` namespace, a
 * malformed PR ref, an `N` beyond the safe range — is a `usage` error before any token
 * work. No `head_sha`/`base_sha`/`trigger_event` is ever sent, and no CI environment
 * variable is read.
 */
function parseCiRef(ciRef: string): RunRefRequest {
  const heads = /^refs\/heads\/(.+)$/.exec(ciRef);
  if (heads !== null) {
    return { kind: 'branch', name: refName(heads[1] ?? '') };
  }
  const tags = /^refs\/tags\/(.+)$/.exec(ciRef);
  if (tags !== null) {
    return { kind: 'tag', name: refName(tags[1] ?? '') };
  }
  const pullRequest = pullRequestRefPattern.exec(ciRef);
  if (pullRequest !== null) {
    // The pattern already refuses a leading zero, a sign and a non-digit. `Number` turns a
    // 400-digit string into `Infinity` and rounds a value past 2^53 - 1 into a different
    // integer, and neither is a safe integer, so this one check covers both (§9).
    const digits = pullRequest[1] ?? '';
    const prNumber = Number(digits);
    if (!Number.isSafeInteger(prNumber) || prNumber < 1) {
      throw usageError(
        `--ci-ref pull request number must be an integer from 1 to ${Number.MAX_SAFE_INTEGER}`,
      );
    }
    return { kind: 'pull_request', name: `${digits}/merge`, pr_number: prNumber };
  }
  throw usageError('--ci-ref must be refs/heads/<name>, refs/tags/<name> or refs/pull/<N>/merge');
}

/** `RunRefRequest.name` is 1..255 code points; a longer name is a `usage` error. */
function refName(name: string): string {
  const length = codePointLength(name);
  if (length === 0 || length > maxRefNameLength) {
    throw usageError(`the --ci-ref name must be 1..${maxRefNameLength} characters`);
  }
  return name;
}

/** Validates the 201 body; anything else is `request_failed` and the body is never echoed. */
function readCreated(data: unknown): { runId: string; state: string } {
  const body: Record<string, unknown> =
    typeof data === 'object' && data !== null ? (data as Record<string, unknown>) : {};
  const runId = body['run_id'];
  const state = body['state'];
  if (typeof runId !== 'string' || !uuidPattern.test(runId)) {
    throw new CliError('the run create response was not valid', { code: 'request_failed' });
  }
  // A hostile or corrupt `state` (a newline, an ANSI escape, anything outside the enum's
  // shape) is a bad body, not something to print (§9 step 3).
  if (typeof state !== 'string' || !statePattern.test(state)) {
    throw new CliError('the run create response was not valid', { code: 'request_failed' });
  }
  return { runId, state };
}
