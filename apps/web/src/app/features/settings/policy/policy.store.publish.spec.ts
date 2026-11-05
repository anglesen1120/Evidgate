// PolicyStore: publication — the local refusals that send nothing, the compact wire body's byte cap,
// `If-Match` from the last list answer, the `412` conflict, and the refresh behind a stored answer.

import { TestBed } from '@angular/core/testing';
import { SessionStore } from '../../../core/session/session.store';
import { PolicyDataService, type PolicyHead } from '../../../core/data/policy-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { PolicyProjectReads } from './policy-reads';
import { PolicyStore } from './policy.store';
import {
  ANCHOR_ID,
  bodyOfSize,
  FakePolicyDataService,
  FakeSessionStore,
  flushMicrotasks,
  HEAD_ID,
  page,
  PROJECT_ID,
  revision,
  SECOND_ID,
  summary,
  typedFailure,
} from './policy.store.testing';

describe('PolicyStore (09.7.1-T4)', () => {
  let data: FakePolicyDataService;
  let session: FakeSessionStore;
  let store: PolicyStore;

  beforeEach(() => {
    data = new FakePolicyDataService();
    session = new FakeSessionStore();
    data.revisions.set(ANCHOR_ID, revision(ANCHOR_ID, PROJECT_ID, 2, 'active'));
    data.pages = [page([summary(ANCHOR_ID, 2, 'active')], null, HEAD_ID)];
    TestBed.configureTestingModule({
      providers: [
        PolicyStore,
        PolicyProjectReads,
        { provide: PolicyDataService, useValue: data },
        { provide: SessionStore, useValue: session },
      ],
    });
    store = TestBed.inject(PolicyStore);
  });

  describe('publish', () => {
    beforeEach(async () => {
      data.publishAnswer = { revision: revision(SECOND_ID, PROJECT_ID, 3, 'draft'), created: true };
      await store.open(ANCHOR_ID);
    });

    it('quotes the last list answer’s ETag in If-Match', async () => {
      const result = await store.publish(bodyOfSize(64));

      expect(data.publishCalls).toEqual([
        { projectId: PROJECT_ID, body: { a: 'x'.repeat(56) }, ifMatch: HEAD_ID },
      ]);
      expect(result.kind).toBe('created');
      if (result.kind === 'created' || result.kind === 'unchanged') {
        expect(result.revision.id).toBe(SECOND_ID);
      }
    });

    it('reads a 200 answer as unchanged', async () => {
      data.publishAnswer = {
        revision: revision(ANCHOR_ID, PROJECT_ID, 2, 'active'),
        created: false,
      };

      const result = await store.publish(bodyOfSize(64));
      await flushMicrotasks();

      expect(result.kind).toBe('unchanged');
      // Nothing was written, so the summary's head read is not issued again.
      expect(data.headCalls).toEqual([PROJECT_ID]);
    });

    it('sends a compact body of exactly 65 536 bytes and refuses 65 537 without a request', async () => {
      const atCap = await store.publish(bodyOfSize(65_536));

      expect(atCap.kind).toBe('created');
      expect(data.publishCalls).toHaveLength(1);

      const overCap = await store.publish(bodyOfSize(65_537));

      expect(overCap).toEqual({ kind: 'too_large', bytes: 65_537 });
      expect(data.publishCalls).toHaveLength(1);
    });

    it('measures the compact body, so a pretty-printed one under the cap is still sent', async () => {
      // The editor seeds itself with the two-space indented document, and indentation is a large
      // part of that text: `critical_cases` with 16 000 members is 64 020 bytes compact and more than
      // twice the cap once every member sits on its own indented line.
      const compact = `{"critical_cases":[${new Array<string>(16_000).fill('"a"').join(',')}]}`;
      const pretty = JSON.stringify(JSON.parse(compact), null, 2);
      expect(new TextEncoder().encode(compact).byteLength).toBe(64_020);
      expect(new TextEncoder().encode(pretty).byteLength).toBeGreaterThan(2 * 65_536);

      const result = await store.publish(pretty);

      expect(result.kind).toBe('created');
      // What travels is the parsed object, so the bytes on the wire are the compact ones.
      expect(data.publishCalls[0]?.body).toEqual(JSON.parse(compact));
      expect(data.publishCalls).toHaveLength(1);
    });

    it('refuses without sending when the compact body cannot be written', async () => {
      // `JSON.stringify` can throw (`RangeError`) on a pathological body, so the measurement
      // lives inside the guarded path and answers "no wire body". The publish then refuses with the
      // answer it already has for unparsable text — and nothing is sent. The body is deeply nested
      // as such a paste is (8 000 levels, well under the 64 KiB cap) and the throw is forced,
      // because the depth at which an engine gives up is not portable across browsers.
      let deep: unknown = 'leaf';
      for (let level = 0; level < 8_000; level += 1) deep = { a: deep };
      const text = JSON.stringify(deep);
      expect(new TextEncoder().encode(text).byteLength).toBeLessThan(65_536);

      const stringify = vi.spyOn(JSON, 'stringify').mockImplementationOnce(() => {
        throw new RangeError('Maximum call stack size exceeded');
      });
      try {
        await expect(store.publish(text)).resolves.toEqual({ kind: 'invalid_json' });
      } finally {
        stringify.mockRestore();
      }

      expect(data.publishCalls).toEqual([]);
    });

    it('refuses a pretty-printed body whose compact form is over the cap, quoting that size', async () => {
      const compact = `{"critical_cases":[${new Array<string>(16_400).fill('"a"').join(',')}]}`;
      const compactBytes = new TextEncoder().encode(compact).byteLength;
      expect(compactBytes).toBe(65_620);

      const result = await store.publish(JSON.stringify(JSON.parse(compact), null, 2));

      // The refusal reports the size the cap is about — the wire body, not the text on screen.
      expect(result).toEqual({ kind: 'too_large', bytes: compactBytes });
      expect(data.publishCalls).toEqual([]);
    });

    it('counts a multi-byte character as every byte it occupies', async () => {
      // `{"note":"` is nine bytes and `"}` is two, and "ế" is three UTF-8 bytes: 21 842 of them are
      // 65 526 bytes, so the compact body is 65 537 bytes while the TEXT is only 21 853 characters
      // long. A character-counted cap would let this through — the cap is declared in bytes, so it is
      // measured in bytes.
      const overCap = `{"note":"${'ế'.repeat(21_842)}"}`;
      expect(overCap.length).toBe(21_853);
      expect(new TextEncoder().encode(overCap).byteLength).toBe(65_537);

      const refused = await store.publish(overCap);

      expect(refused).toEqual({ kind: 'too_large', bytes: 65_537 });
      expect(data.publishCalls).toEqual([]);

      // Two ASCII characters instead of the last three-byte one is exactly 65 536 bytes: accepted.
      const atCap = `{"note":"${'ế'.repeat(21_841)}xx"}`;
      expect(new TextEncoder().encode(atCap).byteLength).toBe(65_536);

      const sent = await store.publish(atCap);

      expect(sent.kind).toBe('created');
      expect(data.publishCalls).toHaveLength(1);
    });

    it.each([
      ['text that is not JSON', 'not json at all'],
      ['a JSON array', '[{"a":1}]'],
      ['a JSON scalar', '42'],
      ['JSON null', 'null'],
    ])('refuses %s as invalid_json without sending anything', async (_label, bodyText) => {
      const result = await store.publish(bodyText);

      expect(result).toEqual({ kind: 'invalid_json' });
      expect(data.publishCalls).toEqual([]);
    });

    it('answers busy for a second call while one is still in flight', async () => {
      data.publishManual = true;

      const first = store.publish(bodyOfSize(64));
      const second = await store.publish(bodyOfSize(64));

      expect(second).toEqual({ kind: 'busy' });
      expect(store.publishing()).toBe(true);
      expect(data.publishCalls).toHaveLength(1);

      data.settlePublish(0, {
        revision: revision(SECOND_ID, PROJECT_ID, 3, 'draft'),
        created: true,
      });
      await expect(first).resolves.toMatchObject({ kind: 'created' });
      expect(store.publishing()).toBe(false);
    });

    it('answers conflict with the head the server named on a 412, and never retries', async () => {
      const currentHead = '018f0000-0000-7000-8000-0000000000c1';
      const conflict = typedFailure(
        {
          error: {
            code: 'precondition_failed',
            message: "the project's policy head changed since it was read",
            request_id: '018f0000-0000-7000-8000-0000000000e6',
            retryable: false,
            details: [
              { path: 'If-Match', reason: 'stale' },
              { path: 'revision_id', reason: currentHead },
            ],
          },
        },
        'precondition_failed',
        412,
      );
      data.publishFailure = conflict;

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('conflict');
      if (result.kind === 'conflict') {
        expect(result.error).toBe(conflict.error);
        expect(result.currentRevisionId).toBe(currentHead);
      }
      // One attempt: a 412 is the end of it, and the reload is the user's explicit choice.
      expect(data.publishCalls).toHaveLength(1);
      // The head on screen is still the one the list answer named, not the server's new one.
      expect(store.headRevisionId()).toBe(HEAD_ID);
    });

    it('carries no current head when the server refused a malformed If-Match', async () => {
      data.publishFailure = typedFailure(
        {
          error: {
            code: 'precondition_failed',
            message: 'If-Match must be the ETag this endpoint issued: a revision id (00 §9.1)',
            request_id: '018f0000-0000-7000-8000-0000000000e7',
            retryable: false,
            details: [{ path: 'If-Match', reason: 'invalid' }],
          },
        },
        'precondition_failed',
        412,
      );

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('conflict');
      if (result.kind === 'conflict') {
        expect(result.currentRevisionId).toBeNull();
      }
    });

    it('hands a 422 back verbatim, with its code, message and field details', async () => {
      const refusal = typedFailure(
        {
          error: {
            code: 'policy_unknown_field',
            message: 'The policy body has a member the schema does not declare.',
            request_id: '018f0000-0000-7000-8000-0000000000e8',
            retryable: false,
            details: [{ path: '/notes', reason: 'additional_properties' }],
          },
        },
        'validation',
        422,
      );
      data.publishFailure = refusal;

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        // The same object the gateway built: the tab renders the server's own code and details.
        expect(result.error).toBe(refusal.error);
        expect(result.error.code).toBe('policy_unknown_field');
        expect(result.error.details).toEqual([{ path: '/notes', reason: 'additional_properties' }]);
      }
    });

    it('surfaces a transport failure as refused with its ApiError and never retries', async () => {
      const network = new ApiRequestError({
        kind: 'network',
        status: 0,
        code: 'network',
        message: 'The request did not reach the server.',
        requestId: null,
        retryable: true,
        details: [],
        retryAfterSeconds: null,
      });
      data.publishFailure = network;

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        expect(result.error).toBe(network.error);
      }
      expect(data.publishCalls).toHaveLength(1);
    });

    it('answers refused with its own copy for a non-server rejection', async () => {
      data.publishFailure = new Error('boom: the caller must not see this');

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        expect(result.error.code).toBe('policy_publish_unresolved');
        expect(result.error.message).not.toContain('boom');
      }
      expect(store.publishing()).toBe(false);
    });

    it('returns the answer before the refresh moves activeRevision', async () => {
      // The opened page names ANCHOR_ID as the active revision; a publish does not change that
      // until a list answer says which row is active.
      expect(store.activeRevision()?.id).toBe(ANCHOR_ID);
      data.listManual = true;

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('created');
      expect(store.activeRevision()?.id).toBe(ANCHOR_ID);
      // The refresh is already out — it is just not awaited by the caller.
      expect(data.pendingLists).toHaveLength(1);
      expect(data.listCalls).toHaveLength(2);

      const newHead = '018f0000-0000-7000-8000-0000000000ca';
      data.settleList(0, page([summary(SECOND_ID, 3, 'active')], null, newHead));
      await flushMicrotasks();

      expect(store.activeRevision()?.id).toBe(SECOND_ID);
      expect(store.headRevisionId()).toBe(newHead);
    });

    it('re-reads the head the answer superseded, so the summary advances with the list', async () => {
      const nextHead = '018f0000-0000-7000-8000-0000000000cb';
      const newRevision = summary(nextHead, 4, 'active');
      data.head = Promise.resolve({ revision: newRevision, headRevisionId: nextHead });

      const result = await store.publish(bodyOfSize(64));
      await flushMicrotasks();

      expect(result.kind).toBe('created');
      // One read at open, one behind the publish answer: the summary names the new head.
      expect(data.headCalls).toEqual([PROJECT_ID, PROJECT_ID]);
      expect(store.reads.head().phase).toBe('ready');
      expect(store.reads.head().value?.id).toBe(nextHead);
    });

    it('answers the publish before the head re-read settles', async () => {
      let settle: (head: PolicyHead) => void = () => undefined;
      data.head = new Promise<PolicyHead>((resolve) => {
        settle = resolve;
      });

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('created');
      expect(store.reads.head().phase).toBe('loading');
      expect(data.headCalls).toEqual([PROJECT_ID, PROJECT_ID]);

      const newRevision = summary('018f0000-0000-7000-8000-0000000000cc', 4, 'active');
      settle({ revision: newRevision, headRevisionId: newRevision.id });
      await flushMicrotasks();

      expect(store.reads.head().value?.id).toBe(newRevision.id);
    });

    it('keeps the stored revision when the refresh behind it fails', async () => {
      data.listManual = true;
      const result = await store.publish(bodyOfSize(64));
      data.failList(0, new Error('boom: the refresh broke'));
      await flushMicrotasks();

      expect(result.kind).toBe('created');
      expect(store.phase()).toBe('ready');
      expect(store.loadError()).toBeNull();
      expect(store.headRevisionId()).toBe(HEAD_ID);
    });

    it('surfaces a refused refresh through the alert without losing the ready list', async () => {
      // The head the screen shows is now stale, so the refusal is not nobody's — but the stored
      // revision and every row stay, because they are still true.
      data.listManual = true;
      const result = await store.publish(bodyOfSize(64));
      data.failList(
        0,
        typedFailure(
          {
            error: {
              code: 'unavailable',
              message: 'The policy store is busy.',
              request_id: '018f0000-0000-7000-8000-0000000000ea',
              retryable: true,
            },
          },
          'unavailable',
          503,
        ),
      );
      await flushMicrotasks();

      expect(result.kind).toBe('created');
      expect(store.loadError()?.code).toBe('unavailable');
      expect(store.phase()).toBe('ready');
      expect(store.revisions().map((row) => row.id)).toEqual([ANCHOR_ID]);
      expect(store.headRevisionId()).toBe(HEAD_ID);

      // The next read that succeeds clears it, so the alert does not outlive the failure.
      data.listManual = false;
      data.pages = [page([summary(SECOND_ID, 3, 'active')], null, SECOND_ID)];
      await store.reload();

      expect(store.loadError()).toBeNull();
      expect(store.headRevisionId()).toBe(SECOND_ID);
    });

    it('refuses without sending when this session does not hold policy:write', async () => {
      // The control is absent from the DOM without the code, so this is the rule repeated where
      // the request is built — nothing is sent, and no request is made to learn what is known.
      session.codes = new Set(['policy:read']);

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        expect(result.error.code).toBe('policy_write_forbidden');
        expect(result.error).toEqual(
          expect.objectContaining({ kind: 'forbidden', status: 0, retryable: false }),
        );
      }
      expect(data.publishCalls).toEqual([]);
      expect(store.publishing()).toBe(false);
    });

    it('answers busy before it looks at the permission, so one press is one outcome', async () => {
      data.publishManual = true;
      const first = store.publish(bodyOfSize(64));

      session.codes = new Set(['policy:read']);
      const second = await store.publish(bodyOfSize(64));

      expect(second).toEqual({ kind: 'busy' });
      data.settlePublish(0, {
        revision: revision(SECOND_ID, PROJECT_ID, 3, 'draft'),
        created: true,
      });
      await expect(first).resolves.toMatchObject({ kind: 'created' });
    });

    it('refuses without sending when no revision is open', async () => {
      store.reset();

      const result = await store.publish(bodyOfSize(64));

      expect(result.kind).toBe('refused');
      if (result.kind === 'refused') {
        expect(result.error.code).toBe('policy_scope_unavailable');
      }
      expect(data.publishCalls).toEqual([]);
    });
  });
});
