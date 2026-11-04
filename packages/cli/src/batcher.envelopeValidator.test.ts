// `createEnvelopeValidator`'s own failure surface: the pointer and reason a rejected line
// reports, what it refuses to echo back, and which schema tree it resolves. The batcher-level
// parity of these verdicts against every contract vector lives in schemaParity.test.ts.
import { join } from 'node:path';

import { describe, expect, it } from 'vitest';

import { EnvelopeInvalidError, createEnvelopeValidator } from './batcher.js';
import {
  contractsDirectory,
  envelopeText,
  eventIdFor,
  repoRoot,
  thrownBy,
  withExtraMember,
} from './batcher.testing.js';

describe('createEnvelopeValidator', () => {
  const validate = createEnvelopeValidator(contractsDirectory);

  /** The `EnvelopeInvalidError` a rejected line throws (never a stray assertion error). */
  function rejection(line: string, sequence: number): EnvelopeInvalidError {
    const error = thrownBy(() => validate(line, sequence));
    expect(error).toBeInstanceOf(EnvelopeInvalidError);
    return error as EnvelopeInvalidError;
  }

  it('accepts a valid envelope line', () => {
    expect(() => validate(envelopeText(12), 12)).not.toThrow();
  });

  it('rejects a schema-invalid envelope, naming the sequence and the event_id', () => {
    const invalid = envelopeText(12).replace('"type":"run.log"', '"type":"Test.Log"');
    const thrown = rejection(invalid, 12);

    expect(thrown.sequence).toBe(12);
    expect(thrown.eventId).toBe(eventIdFor(12));
    expect(thrown.details).toEqual([{ path: '/type', reason: 'pattern' }]);
  });

  it('reports path + reason only — never an event value', () => {
    const secret = 'Sekret-Token-Value';
    const invalid = envelopeText(4).replace('"run.log"', `"${secret}"`);
    const thrown = rejection(invalid, 4);

    expect(thrown.details).toEqual([{ path: '/type', reason: 'pattern' }]);
    expect(thrown.message).not.toContain(secret);
    expect(JSON.stringify(thrown.details)).not.toContain(secret);
  });

  it('rejects a batch-shaped line: one spool line is one envelope', () => {
    const batchLine = `{"events":[${envelopeText(1)}]}`;
    const thrown = rejection(batchLine, 1);

    // The batch's `events` member is unknown to the closed event object; the
    // wrapped `/events/0` prefix never leaks into a row.
    expect(thrown.details).toContainEqual({ path: '/events', reason: 'additional_properties' });
    expect(thrown.details.every((detail) => !detail.path.startsWith('/events/'))).toBe(true);
    // The batch is not a valid event, so it carries no event_id to name.
    expect(thrown.eventId).toBeUndefined();
  });

  it('rejects a line that is not JSON at all', () => {
    const thrown = rejection('{"schema_version":1,', 5);

    expect(thrown.sequence).toBe(5);
    expect(thrown.details).toEqual([{ path: '', reason: 'invalid_json' }]);
    expect(thrown.eventId).toBeUndefined();
  });

  it('caps an echoed member name at 200 code points and escapes control characters', () => {
    const hostileName = `secret_${'a'.repeat(5_000)}`;
    const hostilePath = `/${hostileName}`;
    const thrown = rejection(withExtraMember(7, hostileName), 7);

    // The `details[]` row keeps the exact pointer (machine-readable)…
    expect(thrown.details).toEqual([{ path: hostilePath, reason: 'additional_properties' }]);
    // …while the message echoes at most the first 200 code points of it, then an ellipsis.
    expect(thrown.message).toContain(`${hostilePath.slice(0, 200)}…`);
    expect(thrown.message).not.toContain(hostileName);
    expect(thrown.message.length).toBeLessThan(1_000);

    // A member name carrying a control character is escaped, never emitted raw.
    const controlName = 'bad\u0007name';
    const controlThrown = rejection(withExtraMember(8, controlName), 8);

    expect(controlThrown.details).toEqual([
      { path: `/${controlName}`, reason: 'additional_properties' },
    ]);
    expect(controlThrown.message).not.toContain('\u0007');
    expect(controlThrown.message).toContain('\\u0007');
  });

  it('validates with the packaged schemas when no contracts directory is passed', () => {
    const resolved = createEnvelopeValidator();

    expect(() => resolved(envelopeText(1), 1)).not.toThrow();
  });

  it('fails loud when no contracts tree can be found', () => {
    expect(() => createEnvelopeValidator(join(repoRoot, 'no-such-contracts'))).toThrow(
      /no contract schema/,
    );
  });
});
