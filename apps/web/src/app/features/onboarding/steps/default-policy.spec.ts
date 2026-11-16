// 09.2.1-T5.1 (plan 09.2.1 §10 T5.1, AC5; ruling T62; 07.1.1-T3/T4): the default advisory policy
// is a WIRE BODY, so it is checked against the wire, not against a copy of the wire:
//
// 1. `POST /v1/projects/{p}/policies` declares its request body as
//    `$ref: './schemas/policy-revision.v1.json'` (contracts/openapi.yaml, 07.1.1-T3). This spec
//    asserts that reference, so validating the body against that file IS validating it against the
//    operation's request schema — the step the response validator cannot do, because it only
//    follows local references.
// 2. The body is compiled and validated with `createContractAjv()` from `@evidgate/cli` — the one
//    Ajv 2020-12 + ajv-formats configuration `createOpenApiResponseValidator` and the server-side
//    contract tests share (02 §/T12.2 A). No second validator family, no hand-written rules.
// 3. The activation request body (`{policy_revision_id, mode}`) is inline in the contract, so it is
//    compiled from the same document and the exact body this page sends is validated against it.
//
// A positive result alone would also pass if the schema were compiled vacuously, so two negative
// controls prove the validator is live: an unknown member is refused (the schema is closed,
// `additionalProperties: false`) and a wrong type is refused.
import { existsSync, readFileSync } from 'node:fs';
import { dirname, join } from 'node:path';

import { createContractAjv } from '@evidgate/cli';
import { parse as parseYaml } from 'yaml';

import {
  DEFAULT_ADVISORY_POLICY,
  activationInstantLabel,
  policyHashLabel,
  previewDefaultPolicy,
  sanitizeLabel,
  sanitizedIdentifier,
} from './default-policy';

/**
 * The repository root: `ng test` runs with `apps/web` as its working directory, but a native Vitest
 * run may start anywhere, so the root is the nearest ancestor that holds `contracts/openapi.yaml`
 * (the same helper `core/dev-fixtures/fixture-contract.spec.ts` uses).
 */
function findRepositoryRoot(): string {
  let directory = process.cwd();
  for (;;) {
    if (existsSync(join(directory, 'contracts', 'openapi.yaml'))) {
      return directory;
    }
    const parent = dirname(directory);
    if (parent === directory) {
      throw new Error(
        `cannot locate the repository root (the directory holding contracts/openapi.yaml) from ${process.cwd()}`,
      );
    }
    directory = parent;
  }
}

const REPOSITORY_ROOT = findRepositoryRoot();
const OPENAPI_PATH = join(REPOSITORY_ROOT, 'contracts', 'openapi.yaml');
const POLICY_SCHEMA_PATH = join(REPOSITORY_ROOT, 'contracts', 'schemas', 'policy-revision.v1.json');

/** The reference the create operation must keep naming, so this spec validates the request body. */
const POLICY_SCHEMA_REFERENCE = './schemas/policy-revision.v1.json';

/** A project's revision id as the activation body names it (`format: uuid`). */
const REVISION_ID = '018f0000-0000-7000-8000-00000000a004';

function isRecord(value: unknown): value is Record<string, unknown> {
  return value !== null && typeof value === 'object' && !Array.isArray(value);
}

const DOCUMENT: unknown = parseYaml(readFileSync(OPENAPI_PATH, 'utf8'));

/** The request-body schema node of one operation, from the parsed contract. */
function requestBodySchema(path: string): Record<string, unknown> {
  const paths = isRecord(DOCUMENT) ? DOCUMENT['paths'] : undefined;
  const operation = isRecord(paths) ? paths[path] : undefined;
  const post = isRecord(operation) ? operation['post'] : undefined;
  const requestBody = isRecord(post) ? post['requestBody'] : undefined;
  const content = isRecord(requestBody) ? requestBody['content'] : undefined;
  const json = isRecord(content) ? content['application/json'] : undefined;
  const schema = isRecord(json) ? json['schema'] : undefined;
  if (!isRecord(schema)) {
    throw new Error(`${path} declares no JSON request-body schema`);
  }
  return schema;
}

describe('the default advisory policy body', () => {
  const ajv = createContractAjv();
  // Compiled once: the schema declares an `$id`, and one Ajv instance refuses a second copy of the
  // same id, so a per-test `compile` would fail on the second test, not on a contract change.
  const validatePolicy = ajv.compile(JSON.parse(readFileSync(POLICY_SCHEMA_PATH, 'utf8')));

  it('is the body POST /v1/projects/{p}/policies declares, by reference', () => {
    // If the operation ever stops referencing that file, the validation below would silently stop
    // describing the request body — so the reference itself is the assertion.
    expect(requestBodySchema('/projects/{p}/policies')).toEqual({ $ref: POLICY_SCHEMA_REFERENCE });
  });

  it('validates against contracts/schemas/policy-revision.v1.json (07.1.1-T3)', () => {
    const valid = validatePolicy(DEFAULT_ADVISORY_POLICY);
    // The engine's rows instead of a bare `false`, so a contract change explains itself.
    expect(validatePolicy.errors ?? []).toEqual([]);
    expect(valid).toBe(true);
  });

  it('is refused by the same schema when it carries a member the contract does not declare', () => {
    expect(validatePolicy({ ...DEFAULT_ADVISORY_POLICY, quarantine_budget: 2 })).toBe(false);
    expect(validatePolicy({ ...DEFAULT_ADVISORY_POLICY, late_window_minutes: 'one day' })).toBe(
      false,
    );
  });

  it('carries the T62 late-evidence window and requires nothing else', () => {
    // These literals are the decision, not a default of the schema: T62 fixes the late window at
    // 24 h in the default policy template, and a project that configured nothing must be told that
    // nothing is required of it and no branch is gated.
    expect(DEFAULT_ADVISORY_POLICY).toEqual({
      required_suites: [],
      required_cases: [],
      late_window_minutes: 1440,
      gating_refs: { default_branch: false },
    });
  });

  it('serialises to identical bytes on every send, so the content hash stays the idempotency key', () => {
    // 07.1.1-T3: the server hashes the canonical body. Two attempts must therefore be the same
    // text in the same key order, or a retry would append a second revision instead of replaying.
    // (A `JSON.stringify(x)` vs `JSON.stringify(x)` pair would assert nothing — QA-05.)
    expect(JSON.stringify(DEFAULT_ADVISORY_POLICY)).toBe(
      '{"required_suites":[],"required_cases":[],"late_window_minutes":1440,"gating_refs":{"default_branch":false}}',
    );
  });

  it('sends an activation body the contract declares (07.1.1-T4)', () => {
    const validate = ajv.compile(requestBodySchema('/projects/{p}/policy-activation'));
    expect(validate({ policy_revision_id: REVISION_ID, mode: 'advisory' })).toBe(true);
    // The closed-object and required-member halves must be live for that result to mean anything.
    expect(validate({ mode: 'advisory' })).toBe(false);
    expect(validate({ policy_revision_id: REVISION_ID, mode: 'advisory', note: 'x' })).toBe(false);
    expect(validate({ policy_revision_id: 'not-a-uuid', mode: 'advisory' })).toBe(false);
  });
});

describe('previewDefaultPolicy', () => {
  it('projects the shipped body onto what the read-only preview shows', () => {
    expect(previewDefaultPolicy(DEFAULT_ADVISORY_POLICY)).toEqual({
      lateWindowMinutes: 1440,
      requiredSuites: [],
      requiredCases: [],
      gatesDefaultBranch: false,
    });
  });

  it('reads an omitted member as "nothing set" rather than inventing a value', () => {
    // Absent is not `0`: the contract defines `0` as "no late evidence is accepted" (07.1.1 Q4).
    expect(previewDefaultPolicy({})).toEqual({
      lateWindowMinutes: null,
      requiredSuites: [],
      requiredCases: [],
      gatesDefaultBranch: false,
    });
  });

  it('renders a configured body without changing its values', () => {
    expect(
      previewDefaultPolicy({
        required_suites: ['unit', 'e2e'],
        required_cases: ['STOR-1'],
        late_window_minutes: 0,
        gating_refs: { default_branch: true },
      }),
    ).toEqual({
      lateWindowMinutes: 0,
      requiredSuites: ['unit', 'e2e'],
      requiredCases: ['STOR-1'],
      gatesDefaultBranch: true,
    });
  });
});

describe('policyHashLabel', () => {
  it('preserves every digest character, with or without a sha256 prefix', () => {
    const digest = '9f2c1d7b4a6e8f0a1b2c3d4e5f60718293a4b5c6d7e8f901234567890abcdef0123';
    expect(policyHashLabel(`sha256:${digest}`)).toBe(`sha256:${digest}`);
    expect(policyHashLabel(digest)).toBe(digest);
  });

  it('removes invisible characters without abbreviating the remaining provenance', () => {
    expect(policyHashLabel(' sha256:9f2c\u202E\u200D1d7b4a6e8f0a ')).toBe(
      'sha256:9f2c1d7b4a6e8f0a',
    );
  });
});

describe('sanitizeLabel', () => {
  it.each([
    ['\u202E', 'the bidi right-to-left override'],
    ['\u202D', 'the bidi left-to-right override'],
    ['\u200E', 'the left-to-right mark'],
    ['\u200D', 'the zero-width joiner'],
    ['\u{E0001}', 'the astral language tag'],
    ['\u001B', 'the C0 escape'],
    ['\u009B', 'the C1 control-sequence introducer'],
    ['\u2028', 'a line separator'],
    ['\u2029', 'a paragraph separator'],
  ])('removes %s (%s)', (character) => {
    expect(sanitizeLabel(`sha256:9f${character}2c`)).toBe('sha256:9f2c');
  });

  it('keeps ordinary text, an emoji and a combining mark', () => {
    // Nothing here is a control, a format character or a line separator, so nothing is removed.
    expect(sanitizeLabel('revision 1 — ổn 🚀 e\u0301')).toBe('revision 1 — ổn 🚀 e\u0301');
  });

  it('removes a zero-width space too, because it is a format character (Cf)', () => {
    // U+200B carries no information here and is invisible: removing it is the same decision as for
    // the joiners above, and it keeps the digest's characters counted as characters.
    expect(sanitizeLabel('a\u200Bb')).toBe('ab');
  });

  it('produces text with no control or format character left at all', () => {
    const cleaned = sanitizeLabel('a\u0000b\u001Fc\u202Ed\u200Ee\u{E0001}f');
    expect(cleaned).toBe('abcdef');
    expect([...cleaned].some((character) => /[\p{Cc}\p{Cf}]/u.test(character))).toBe(false);
  });
});

describe('the display guards for a member the answer may not carry (F5)', () => {
  it('reads a sanitised identifier, and refuses everything that is not a non-blank string', () => {
    expect(sanitizedIdentifier('018f0000-0000-7000-8000-00000000a003')).toBe(
      '018f0000-0000-7000-8000-00000000a003',
    );
    // A format character is stripped before the value is shown (T47 precedent).
    expect(sanitizedIdentifier('abc\u202Edef')).toBe('abcdef');
    expect(sanitizedIdentifier('   ')).toBeNull();
    expect(sanitizedIdentifier('')).toBeNull();
    expect(sanitizedIdentifier(null)).toBeNull();
    expect(sanitizedIdentifier(undefined)).toBeNull();
    expect(sanitizedIdentifier(42)).toBeNull();
  });

  it('keeps a readable hash whole and answers null for one it cannot read', () => {
    const hash = 'sha256:9f2c1d7b4a6e8f0a1b2c3d4e5f60718293a4b5c6d7e8f901234';
    expect(policyHashLabel(hash)).toBe(hash);
    expect(policyHashLabel(undefined)).toBeNull();
    expect(policyHashLabel({ hash: 'x' })).toBeNull();
  });

  it('accepts only an instant the date pipe can render, so the pipe never throws', () => {
    expect(activationInstantLabel('2026-10-27T01:00:00Z')).toBe('2026-10-27T01:00:00Z');
    // `DatePipe` throws on these; the panel must render "unavailable" instead of breaking.
    expect(activationInstantLabel('not a date')).toBeNull();
    for (const lenient of ['1', '2026', '2026-13-01T00:00:00Z']) {
      expect(activationInstantLabel(lenient)).toBeNull();
    }
    // A calendar-impossible instant is refused too, so the pipe never renders the next day.
    expect(activationInstantLabel('2026-02-30T00:00:00Z')).toBeNull();
    expect(activationInstantLabel('2026-10-27T08:00:00+07:00')).toBe('2026-10-27T08:00:00+07:00');
    expect(activationInstantLabel('')).toBeNull();
    expect(activationInstantLabel(null)).toBeNull();
  });
});
