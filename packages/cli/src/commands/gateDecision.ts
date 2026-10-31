// The gate verdict: what one `Decision` body means for the process exit code, and the two
// output forms. Only a literal `PASS` may exit 0, and a body this module cannot read is a
// request error, so no unreadable, unknown or missing decision can ever pass a build.
import { CliError } from '../errors.js';
import { ExitCode } from '../exitCodes.js';
import { authorizationHeaderShape, redact, redacted } from '../redact.js';
import { sanitizeServerText } from '../serverError.js';

/** The verdicts openapi `Decision` can carry; `null` in that member means still pending. */
export type GateDecisionValue = 'PASS' | 'FAIL' | 'INCONCLUSIVE';

/**
 * One parsed decision. The four trailing members are absent from the contract today, so they
 * stay `null` unless the server sends them as strings; nothing here derives or guesses them.
 */
export interface GateOutcome {
  readonly decision: GateDecisionValue | null;
  readonly completeness: string | null;
  readonly evaluationId: string | null;
  readonly mode: string | null;
  readonly url: string | null;
  readonly reasons: readonly string[];
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

/** Parses one parsed body; `decision` must be present and one of the four accepted values. */
export function parseDecisionBody(parsed: unknown): GateOutcome {
  if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
    throw invalidBody('the body was not an object');
  }
  const record = parsed as Record<string, unknown>;
  if (!('decision' in record)) {
    throw invalidBody('the body carried no decision');
  }
  const raw = record['decision'];
  if (raw !== null && !isDecisionValue(raw)) {
    // The value itself is never echoed: it is server text and could forge a log line.
    throw invalidBody('the decision was not one of PASS, FAIL, INCONCLUSIVE or null');
  }
  return {
    decision: raw,
    completeness: optionalString(record, 'completeness'),
    evaluationId: optionalString(record, 'evaluation_id'),
    mode: optionalString(record, 'mode'),
    url: optionalString(record, 'url'),
    reasons: reasonsOf(record),
  };
}

/** An optional member: only a string is printed, every other shape is `null`, never a guess. */
function optionalString(record: Record<string, unknown>, member: string): string | null {
  const value = record[member];
  return typeof value === 'string' ? value : null;
}

function reasonsOf(record: Record<string, unknown>): readonly string[] {
  const value = record['reasons'];
  if (value === undefined) {
    return [];
  }
  if (!Array.isArray(value)) {
    throw invalidBody('the reasons were not a list of strings');
  }
  const reasons: string[] = [];
  for (const entry of value) {
    if (typeof entry !== 'string') {
      throw invalidBody('the reasons were not a list of strings');
    }
    reasons.push(entry);
  }
  return reasons;
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
 * The `--json` line: exactly these five members, in this order, so a consumer can parse the
 * line without knowing whether the run decided. `decision` is `null` while pending. The
 * optional members are printed as the server sent them, apart from redaction: each is
 * redacted before it is encoded, so a secret inside one can neither be printed nor shorten
 * the rest of the line. The format and line characters `JSON.stringify` leaves raw are
 * escaped, so no member can break the line or override its rendering.
 */
export function jsonLine(outcome: GateOutcome): string {
  const line = JSON.stringify({
    decision: outcome.decision,
    completeness: redactMember(outcome.completeness),
    evaluation_id: redactMember(outcome.evaluationId),
    mode: redactMember(outcome.mode),
    url: redactMember(outcome.url),
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
export function reasonLines(reasons: readonly string[]): string {
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
  return value === null ? null : redact(value).replace(authorizationHeaderShape, () => redacted);
}
