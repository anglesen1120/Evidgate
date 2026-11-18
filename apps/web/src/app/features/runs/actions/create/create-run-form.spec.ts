import type { ApiError } from '../../../../core/http/api-error';
import {
  EMPTY_CREATE_RUN_DRAFT,
  createRunBody,
  createRunFieldError,
  createRunRefusal,
} from './create-run-form';

const MANIFEST = '018f0000-0000-7000-8000-000000000011';
const RELEASE = '018f0000-0000-7000-8000-000000000012';
const failure = (patch: Partial<ApiError>): ApiError => ({
  kind: 'validation',
  status: 422,
  code: 'request_invalid',
  message: 'Invalid request',
  requestId: 'request-1',
  retryable: false,
  retryAfterSeconds: null,
  details: [],
  ...patch,
});

describe('create-run form contract', () => {
  it('sends only the required manifest when optional fields are empty', () => {
    expect(createRunBody({ ...EMPTY_CREATE_RUN_DRAFT, manifestId: MANIFEST })).toEqual({
      manifest_id: MANIFEST,
    });
  });

  it('preserves the complete supported identity and build inputs', () => {
    expect(
      createRunBody({
        ...EMPTY_CREATE_RUN_DRAFT,
        manifestId: MANIFEST,
        releaseId: RELEASE,
        purpose: 'replay',
        replayGrantId: MANIFEST,
        kind: 'pull_request',
        refName: '42/merge',
        prNumber: '42',
        headSha: 'a'.repeat(40),
        baseSha: 'b'.repeat(64),
        triggerEvent: 'pull_request',
        isFork: true,
        components: '{"web":{"digest":"sha256:abc"}}',
      }),
    ).toEqual({
      manifest_id: MANIFEST,
      release_id: RELEASE,
      purpose: 'replay',
      replay_grant_id: MANIFEST,
      components: { web: { digest: 'sha256:abc' } },
      ref: {
        kind: 'pull_request',
        name: '42/merge',
        pr_number: 42,
        head_sha: 'a'.repeat(40),
        base_sha: 'b'.repeat(64),
        trigger_event: 'pull_request',
        is_fork: true,
      },
    });
  });

  it.each(['', 'not-a-uuid'])('refuses an invalid manifest id %s', (manifestId) => {
    expect(
      createRunFieldError('manifestId', { ...EMPTY_CREATE_RUN_DRAFT, manifestId }, null),
    ).toMatch(/manifest|UUID/i);
  });

  it('accepts both SHA lengths and requires a positive PR number for a pull request', () => {
    const draft = {
      ...EMPTY_CREATE_RUN_DRAFT,
      manifestId: MANIFEST,
      kind: 'pull_request' as const,
    };
    expect(createRunFieldError('prNumber', draft, null)).toMatch(/number/i);
    expect(createRunFieldError('prNumber', { ...draft, prNumber: '0' }, null)).toMatch(/number/i);
    expect(createRunFieldError('prNumber', { ...draft, prNumber: '1' }, null)).toBeNull();
    for (const size of [40, 64]) {
      expect(
        createRunFieldError('headSha', { ...draft, headSha: 'a'.repeat(size) }, null),
      ).toBeNull();
    }
    expect(createRunFieldError('headSha', { ...draft, headSha: 'a'.repeat(41) }, null)).toMatch(
      /40|64/,
    );
  });

  it.each([255, 256])('counts %s supplementary characters as Unicode code points', (length) => {
    const draft = { ...EMPTY_CREATE_RUN_DRAFT, refName: '𠮷'.repeat(length) };
    const error = createRunFieldError('refName', draft, null);
    if (length === 255) {
      expect(error).toBeNull();
      expect(createRunBody(draft).ref?.name).toBe(draft.refName);
    } else {
      expect(error).toMatch(/255/);
    }
  });

  it('refuses replay locally while regular creation remains available', () => {
    expect(createRunFieldError('purpose', EMPTY_CREATE_RUN_DRAFT, null)).toBeNull();
    expect(
      createRunFieldError('purpose', { ...EMPTY_CREATE_RUN_DRAFT, purpose: 'replay' }, null),
    ).toMatch(/replay grants are not available/i);
  });

  it.each(['{broken', 'null', '[]', '1'])('refuses non-object components %s', (components) => {
    expect(
      createRunFieldError('components', { ...EMPTY_CREATE_RUN_DRAFT, components }, null),
    ).toMatch(/JSON object/i);
  });

  it('maps JSON pointer and dotted ref paths to the same inline field', () => {
    for (const path of ['/ref/name', 'ref.name']) {
      const error = failure({
        message: 'Use a shorter ref.',
        details: [{ path, reason: 'invalid_value' }],
      });
      expect(
        createRunFieldError('refName', { ...EMPTY_CREATE_RUN_DRAFT, manifestId: MANIFEST }, error),
      ).toContain('Use a shorter ref.');
    }
  });

  it.each([
    ['manifest_invalid', 'manifest'],
    ['execution_key_conflict', 'execution'],
    ['release_unresolvable', 'release'],
    ['release_not_active', 'release'],
    ['fork_run_not_bindable', 'fork'],
    ['replay_grant_invalid', 'replay'],
    ['replay_release_not_allowed', 'replay'],
    ['request_invalid', 'fields'],
    ['cap_exceeded', 'limit'],
  ])('explains the typed %s refusal without inventing success', (code, text) => {
    expect(createRunRefusal(failure({ code })).toLowerCase()).toContain(text);
  });

  it('distinguishes conflicting keys, unavailable identifiers, forbidden writes and oversized bodies', () => {
    expect(createRunRefusal(failure({ kind: 'idempotency_conflict', status: 409 }))).toMatch(
      /key|intent/i,
    );
    expect(createRunRefusal(failure({ kind: 'not_found', status: 404 }))).toMatch(
      /manifest|release/i,
    );
    expect(createRunRefusal(failure({ kind: 'forbidden', status: 403 }))).toMatch(/permission/i);
    expect(createRunRefusal(failure({ kind: 'payload_too_large', status: 413 }))).toMatch(/64 KiB/);
  });
});
