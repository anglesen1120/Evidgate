// The refusals the widened `Decision` body adds beyond `status`/`decision`: a `missing` entry
// outside the closed eight codes, any evaluation member filled on a pending body, an evaluated
// `mode` other than `live`, and an `evidence_revision` that is not an integer.
import { afterEach, beforeEach, describe, expect, it } from 'vitest';

import { resetSecrets } from '../redact.js';

import { devStubFetch } from './devExchange.testing.js';
import { parseDecisionBody } from './gateDecision.js';
import {
  cacheDirs,
  capture,
  decided,
  devEnv,
  evaluated,
  gateArgv,
  invoke,
  pending,
  pendingOutcome,
  runId,
} from './gate.kit.testing.js';

/** The closed `missing[]` list of 00 §8.5, in the wire order the server sends it. */
const closedCodes = [
  'missing_shard',
  'count_mismatch',
  'artifact_missing',
  'build_mismatch',
  'unknown_status',
  'setup_error',
  'stale_epoch',
  'missing_result',
];

/**
 * Every member a pending body must leave `null`, with a value that is non-null even when it is
 * falsey: the rule is about `null`, not about truthiness.
 */
const filledMembers: ReadonlyArray<readonly [string, unknown]> = [
  ['evaluation_id', 'pending-eval-1'],
  ['evidence_revision', 0],
  ['completeness', false],
  ['outcome', 0],
  ['missing', []],
  ['reasons', []],
  ['policy_hash', ''],
  ['manifest_hash', false],
  ['evaluator_version', 0],
  ['mode', 'live'],
];

/** The message of the refusal `body` earns; a body that is accepted fails the spec. */
function refusalMessage(body: Record<string, unknown>): string {
  try {
    parseDecisionBody(body);
  } catch (error) {
    return error instanceof Error ? error.message : String(error);
  }
  throw new Error('the body was accepted');
}

/**
 * The full refusal text for `reason`. Asserting equality rather than a fragment is what proves
 * a refused body's own value never reached the message.
 */
function refusalFor(reason: string): string {
  return `the run decision response was not valid: ${reason}`;
}

describe('gate decision body', () => {
  const dirs = cacheDirs();

  beforeEach(() => {
    resetSecrets();
  });

  afterEach(async () => {
    await dirs.reset();
  });

  describe('missing codes', () => {
    it('keeps the closed eight codes in the order the server sent them', () => {
      const outcome = parseDecisionBody(evaluated({ decision: 'FAIL', missing: closedCodes }));

      expect(outcome.missing).toStrictEqual(closedCodes);
    });

    it('refuses a code outside the closed eight without echoing it', () => {
      const message = refusalMessage(
        evaluated({ decision: 'FAIL', missing: ['missing_shard', 'shard_timeout'] }),
      );

      // The code is server text and could forge a log line, so only the rule is named.
      expect(message).toBe(refusalFor('the missing list carried a code outside the closed eight'));
    });
  });

  describe('a pending body', () => {
    it('refuses one that fills any evaluation member', () => {
      for (const [member, value] of filledMembers) {
        expect(refusalMessage(pending({ [member]: value }))).toBe(
          refusalFor(`a pending body carried a ${member}`),
        );
      }
    });

    it('keeps one whose only non-null member is the url', () => {
      const url = 'https://evidgate.test/projects/p1/runs/r1';

      expect(parseDecisionBody(pending({ url }))).toStrictEqual({ ...pendingOutcome, url });
    });
  });

  describe('the mode of an evaluated body', () => {
    it('keeps a live mode', () => {
      expect(parseDecisionBody(evaluated({ decision: 'PASS', mode: 'live' })).mode).toBe('live');
    });

    it('accepts a body that carries no mode at all', () => {
      const body = evaluated({ decision: 'PASS' });
      delete body['mode'];

      expect(parseDecisionBody(body).mode).toBeNull();
    });

    it('refuses any other mode without echoing it', () => {
      for (const value of ['shadow', 'LIVE', 5, true]) {
        const message = refusalMessage(evaluated({ decision: 'PASS', mode: value }));

        expect(message).toBe(refusalFor('the mode was not live'));
      }
    });
  });

  describe('evidence_revision', () => {
    it('prints an integer as sent', () => {
      for (const value of [7, 0]) {
        expect(
          parseDecisionBody(evaluated({ decision: 'PASS', evidence_revision: value }))
            .evidenceRevision,
        ).toBe(value);
      }
    });

    it('prints null for a revision that is not an integer, never a rounded value', () => {
      for (const value of [7.5, 1e-7, '7', null]) {
        expect(
          parseDecisionBody(evaluated({ decision: 'PASS', evidence_revision: value }))
            .evidenceRevision,
        ).toBeNull();
      }
    });
  });

  describe('the exit code of a refused body', () => {
    const cases: ReadonlyArray<readonly [string, () => Record<string, unknown>, string]> = [
      [
        'a missing code outside the closed eight',
        () => evaluated({ decision: 'PASS', missing: ['shard_timeout'] }),
        'shard_timeout',
      ],
      [
        'a pending body with an evaluation member',
        () => pending({ evaluation_id: 'pending-eval-1' }),
        'pending-eval-1',
      ],
      [
        'an evaluated mode that is not live',
        () => evaluated({ decision: 'PASS', mode: 'shadow' }),
        'shadow',
      ],
    ];

    for (const [label, body, hidden] of cases) {
      it(`exits 3 for ${label}, printing nothing`, async () => {
        const env = await devEnv(dirs);
        const stub = devStubFetch(() => decided(body()));
        const out = capture();

        const { code } = await invoke(
          gateArgv(['--run', runId, '--json']),
          env,
          out,
          stub.fetchImpl,
        );

        expect(code).toBe(3);
        // A refused body is a contract violation, not a slow server: no retry, no verdict.
        expect(stub.calls).toHaveLength(1);
        expect(out.stdout.join('')).toBe('');
        expect(out.stderr.join('')).toContain('was not valid');
        expect(out.stderr.join('')).not.toContain(hidden);
      });
    }
  });

  it('prints the url of a pending body and exits 2', async () => {
    const env = await devEnv(dirs);
    const url = 'https://evidgate.test/projects/p1/runs/r1';
    const stub = devStubFetch(() => decided(pending({ url })));
    const out = capture();

    const { code } = await invoke(gateArgv(['--run', runId, '--json']), env, out, stub.fetchImpl);

    expect(code).toBe(2);
    expect(JSON.parse(out.stdout.join(''))).toMatchObject({ status: 'pending', url });
  });
});
