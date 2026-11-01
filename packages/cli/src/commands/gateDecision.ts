// The gate verdict: what one `Decision` body means for the process exit code, and the two
// output forms. Only a literal `PASS` may exit 0, and a body whose `status` and `decision`
// disagree, or that this module cannot read, is a request error.
import type { Decision } from '@evidgate/sdk';

import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { authorizationHeaderShape, redact, redacted } from '../redact.js';
import { sanitizeServerText } from '../serverError.js';

/** The verdicts openapi `Decision` can carry; `null` in that member means still pending. */
export type GateDecisionValue = NonNullable<Decision['decision']>;
/** Whether the run has a live evaluation row at all. */
export type GateDecisionStatus = Decision['status'];

/** One parsed decision, in the body's own order; this parser is its only writer. */
export interface GateOutcome {
  readonly status: GateDecisionStatus;
  readonly decision: GateDecisionValue | null;
  readonly completeness: string | null;
  readonly evaluationId: string | null;
  readonly evidenceRevision: number | null;
  readonly outcome: string | null;
  readonly missing: readonly string[] | null;
  readonly reasons: readonly string[] | null;
  readonly policyHash: string | null;
  readonly manifestHash: string | null;
  readonly evaluatorVersion: string | null;
  readonly mode: string | null;
  readonly url: string | null;
}

/** A reason line is capped to this many code points, like the server-text cap elsewhere. */
const maxReasonLength = 200;
/** At most this many reason lines are printed; the rest collapse into one `(k more)` line. */
const maxReasonLines = 50;

const decisionValues: readonly GateDecisionValue[] = ['PASS', 'FAIL', 'INCONCLUSIVE'];

function isDecisionValue(value: unknown): value is GateDecisionValue {
  return decisionValues.some((candidate) => candidate === value);
}

function invalidBody(reason: string): CliError {
  return new CliError(`the run decision response was not valid: ${reason}`, {
    code: 'request_failed',
  });
}

/**
 * Parses the response text: malformed JSON, a non-object body and an unknown `decision` are
 * all refused, because a gate that cannot read the verdict must not report one.
 */
export function parseDecisionText(text: unknown): GateOutcome {
  if (typeof text !== 'string') {
    throw invalidBody('the body was not text');
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(text);
  } catch {
    throw invalidBody('the body was not JSON');
  }
  return parseDecisionBody(parsed);
}

/**
 * Parses one parsed body. `status` is required and decides which `decision` is allowed, so a
 * pending body can never be read as a verdict and an evaluated body always carries one.
 */
export function parseDecisionBody(parsed: unknown): GateOutcome {
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw invalidBody('the body was not an object');
  }
  const record = parsed as Record<string, unknown>;
  const status = statusOf(record);
  if (!('decision' in record)) {
    throw invalidBody('the body carried no decision');
  }
  const raw = record['decision'];
  if (raw !== null && !isDecisionValue(raw)) {
    // The value itself is never echoed: it is server text and could forge a log line.
    throw invalidBody('the decision was not one of PASS, FAIL, INCONCLUSIVE or null');
  }
  if (status === 'pending' && raw !== null) {
    throw invalidBody('a pending body carried a decision');
  }
  if (status === 'evaluated' && raw === null) {
    throw invalidBody('an evaluated body carried no decision');
  }
  return {
    status,
    decision: raw,
    completeness: optionalString(record, 'completeness'),
    evaluationId: optionalString(record, 'evaluation_id'),
    evidenceRevision: optionalNumber(record, 'evidence_revision'),
    outcome: optionalString(record, 'outcome'),
    missing: stringListOrNull(record, 'missing'),
    reasons: stringListOrNull(record, 'reasons'),
    policyHash: optionalString(record, 'policy_hash'),
    manifestHash: optionalString(record, 'manifest_hash'),
    evaluatorVersion: optionalString(record, 'evaluator_version'),
    mode: optionalString(record, 'mode'),
    url: optionalString(record, 'url'),
  };
}

/** A required member whose value is never echoed; only the refusal names the member. */
function statusOf(record: Record<string, unknown>): GateDecisionStatus {
  if (!('status' in record)) {
    throw invalidBody('the body carried no status');
  }
  const status = record['status'];
  if (status !== 'pending' && status !== 'evaluated') {
    throw invalidBody('the status was not pending or evaluated');
  }
  return status;
}

/** An optional member: only a string is printed, every other shape is `null`, never a guess. */
function optionalString(record: Record<string, unknown>, member: string): string | null {
  const value = record[member];
  return typeof value === 'string' ? value : null;
}

/** An optional count: only a finite number is printed, every other shape is `null`. */
function optionalNumber(record: Record<string, unknown>, member: string): number | null {
  const value = record[member];
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/** A nullable list of strings: `null`/absent stays `null`, any other shape is refused. */
function stringListOrNull(
  record: Record<string, unknown>,
  member: string,
): readonly string[] | null {
  const value = record[member];
  if (value === undefined || value === null) {
    return null;
  }
  if (!Array.isArray(value)) {
    throw invalidBody(`the ${member} were not a list of strings`);
  }
  const entries: string[] = [];
  for (const entry of value) {
    if (typeof entry !== 'string') {
      throw invalidBody(`the ${member} were not a list of strings`);
    }
    entries.push(entry);
  }
  return entries;
}

/** The one place a verdict becomes an exit code; a pending run is `Inconclusive`, never `Ok`. */
export function exitCodeForDecision(decision: GateDecisionValue | null): ExitCode {
  switch (decision) {
    case 'PASS':
      return ExitCode.Ok;
    case 'FAIL':
      return ExitCode.Fail;
    case 'INCONCLUSIVE':
    case null:
      return ExitCode.Inconclusive;
  }
}

/**
 * The `--json` line: the five keys the gate has always printed, in that order, then the
 * members the widened `Decision` body adds, so a consumer of the old line keeps parsing it.
 * Each value is printed as the server sent it, apart from redaction: a string member is
 * redacted, then encoded, and the format and line characters `JSON.stringify` leaves raw are
 * escaped, so no member can break the line or override its rendering.
 */
export function jsonLine(outcome: GateOutcome): string {
  const line = JSON.stringify({
    decision: outcome.decision,
    completeness: redactMember(outcome.completeness),
    evaluation_id: redactMember(outcome.evaluationId),
    mode: redactMember(outcome.mode),
    url: redactMember(outcome.url),
    status: outcome.status,
    evidence_revision: outcome.evidenceRevision,
    outcome: redactMember(outcome.outcome),
    missing: redactList(outcome.missing),
    reasons: redactList(outcome.reasons),
    policy_hash: redactMember(outcome.policyHash),
    manifest_hash: redactMember(outcome.manifestHash),
    evaluator_version: redactMember(outcome.evaluatorVersion),
  });
  return `${line.replace(
    /[\p{Cf}\p{Zl}\p{Zp}]/gu,
    (character) => `\\u${(character.codePointAt(0) ?? 0).toString(16).padStart(4, '0')}`,
  )}\n`;
}

/** `gate: <DECISION> (run <id>)` — the one line a human reads in a CI log. */
export function verdictLine(decision: GateDecisionValue, runId: string): string {
  return `gate: ${decision} (run ${runId})\n`;
}

/**
 * The reasons list, one indented line each. Server text is redacted, control-char escaped and
 * capped, and the count is capped like the server's own detail lines, so one response can
 * neither forge a second line nor flood the log.
 */
export function reasonLines(reasons: readonly string[] | null): string {
  if (reasons === null) {
    return '';
  }
  const shown = reasons.slice(0, maxReasonLines);
  const lines = shown
    .map((reason) => `  - ${sanitizeServerText(reason, maxReasonLength)}\n`)
    .join('');
  const hidden = reasons.length - shown.length;
  return hidden > 0 ? `${lines}  … (${hidden} more)\n` : lines;
}

/**
 * A member is redacted on its own, then loses any whole `Authorization:` shape: the writer's
 * header rule runs again over the finished line and consumes to its end, so a member that
 * still reads like a header would swallow the closing quotes and braces of the object.
 */
function redactMember(value: string | null): string | null {
  return value === null ? null : redactText(value);
}

/** One already-typed member text, redacted and stripped of the header shape. */
function redactText(value: string): string {
  return redact(value).replace(authorizationHeaderShape, () => redacted);
}

/** A nullable list, redacted per entry so one member cannot hide another. */
function redactList(values: readonly string[] | null): readonly string[] | null {
  return values === null ? null : values.map((value) => redactText(value));
}
