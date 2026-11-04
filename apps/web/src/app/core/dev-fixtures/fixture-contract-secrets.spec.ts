// The one-time secret pins: a minting operation answers its secret once, no other document carries
// one, and the invite lifecycle's replay documents differ in the token alone. The document reader
// and the body scan are shared from the corpus helper.

import {
  CORPUS,
  corpusDocument,
  oneTimeSecretMembers,
  readDocument,
} from './fixture-contract.corpus';

/** The operations that mint one, and the only ones allowed to answer with a secret member. */
const SECRET_MINTING_OPERATIONS: readonly string[] = [
  'createCiBinding',
  'createIngestToken',
  'rotateIngestToken',
  'createMembership',
  'resendMembershipInvite',
];

/** The seed page that derives step 3 from a token: one active token, its metadata and no secret. */
const ONBOARDING_TOKEN_FIXTURE = 'ingest-tokens-list-onboarding-token';

/**
 * Assert a corpus `invite_token` is an obviously fake placeholder: the `evgi_` prefix the contract
 * documents, the corpus's own `FIXTURE_ONLY` marker, one dot-free segment so it cannot read as a
 * JWT, and no `://` — the corpus carries a raw token, never a link a reviewer could paste.
 */
function expectInvitePlaceholder(value: string | null): void {
  expect(value, 'the invite answer carries no token at all').not.toBeNull();
  expect(value?.startsWith('evgi_')).toBe(true);
  expect(value).toContain('FIXTURE_ONLY');
  expect(value?.split('.')).toHaveLength(1);
  expect(value).not.toContain('://');
}

/**
 * A body without its `invite_token`, so a replay document can be compared with the answer it
 * stored: the two must differ in the token alone.
 */
function withoutInviteToken(body: unknown): Record<string, unknown> {
  const copy: Record<string, unknown> = { ...(body as Record<string, unknown>) };
  delete copy['invite_token'];
  return copy;
}
describe('Fixture contract check (09.1.1-T12.2, AC10)', () => {
  it('answers the one-time step-2 secrets in the create documents alone (09.2.1-T2)', () => {
    // 1. Every document of an operation that mints no secret is secret-free. This is AC2's
    //    "metadata only on resume" on the wire: the wizard's lists and detail reads can only show
    //    metadata when the corpus itself has no member that could carry a secret.
    for (const fixture of CORPUS) {
      const document = readDocument(fixture);
      if (SECRET_MINTING_OPERATIONS.includes(document.operation_id)) {
        continue;
      }
      expect(
        oneTimeSecretMembers(document.body),
        `fixtures/web-api/${fixture.name} (${document.operation_id}) answers with a one-time secret; only ${SECRET_MINTING_OPERATIONS.join(', ')} mint one`,
      ).toEqual([]);
    }

    // 2. The three creates do carry theirs, and their values are obviously fake: the corpus has no
    //    real issuer, and 00 §5.7 S3 forbids a credential-shaped value.
    const bindingCreate = corpusDocument('ci-binding-create');
    expect([bindingCreate.operation_id, bindingCreate.status]).toEqual(['createCiBinding', 201]);
    const bindingBody = bindingCreate.body as {
      binding: { status: string };
      binding_challenge: string;
    };
    expect(bindingBody.binding.status).toBe('pending_verification');
    expect(bindingBody.binding_challenge).toContain('fixture_only');
    // A JWT carries three dot-separated segments; a challenge that looked like one would read as
    // a bearer credential in a page that shows it.
    expect(bindingBody.binding_challenge.split('.')).toHaveLength(1);
    expect(bindingBody.binding_challenge).not.toContain('eyJ');

    const tokenCreate = corpusDocument('ingest-token-create');
    expect([tokenCreate.operation_id, tokenCreate.status]).toEqual(['createIngestToken', 201]);
    const tokenBody = tokenCreate.body as {
      token: { status: string };
      secret: string;
      overlap_ends_at: string | null;
    };
    expect(tokenBody.token.status).toBe('active');
    expect(tokenBody.secret.startsWith('egt_')).toBe(true);
    expect(tokenBody.secret).toContain('FIXTURE_ONLY');
    expect(tokenBody.secret.split('.')).toHaveLength(1);
    // A freshly issued token retires nothing, so there is no overlap window yet.
    expect(tokenBody.overlap_ends_at).toBeNull();

    const tokenRotate = corpusDocument('ingest-token-rotate');
    expect([tokenRotate.operation_id, tokenRotate.status]).toEqual(['rotateIngestToken', 200]);
    const rotatedBody = tokenRotate.body as {
      token: { overlap_ends_at: string | null };
      secret: string;
      overlap_ends_at: string | null;
    };
    expect(rotatedBody.secret.startsWith('egt_')).toBe(true);
    expect(rotatedBody.secret).toContain('FIXTURE_ONLY');
    // A rotation is the one answer that carries a window: the retired token keeps verifying until
    // `overlap_ends_at`, both on the successor (its own retirement date) and at the top level.
    expect(rotatedBody.overlap_ends_at).not.toBeNull();
    expect(rotatedBody.token.overlap_ends_at).toBe(rotatedBody.overlap_ends_at);

    // 3. The seed that derives step 3 from a token shows exactly one active token's metadata.
    const tokenPage = corpusDocument(ONBOARDING_TOKEN_FIXTURE);
    expect([tokenPage.operation_id, tokenPage.variant]).toEqual([
      'listIngestTokens',
      'onboarding-token',
    ]);
    const page = tokenPage.body as { items: { status: string }[]; next_cursor: string | null };
    expect(page.items).toHaveLength(1);
    expect(page.items[0]?.status).toBe('active');
    expect(page.next_cursor).toBeNull();
  });

  it('answers the invite token once and only in the invite documents (09.7.1-T3)', () => {
    // The corpus page's `pending` row is the membership both lifecycle calls address, so the tab has
    // a row to resend or cancel without a second list document.
    const membersPage = corpusDocument('memberships-list').body as {
      items: { id: string; status: string }[];
    };
    const pendingRow = membersPage.items.find((item) => item.status === 'pending');
    expect(
      pendingRow,
      'memberships-list carries no pending membership, so the invite lifecycle documents address a row no page shows',
    ).toBeDefined();

    // 1. `createMembership`'s 201 carries the token, and its `replay` twin is that body without it.
    const created = corpusDocument('membership-create-ok');
    expect([created.operation_id, created.status, created.variant]).toEqual([
      'createMembership',
      201,
      'ok',
    ]);
    const createdBody = created.body as { status: string; invite_token: string | null };
    expect(createdBody.status).toBe('pending');
    expectInvitePlaceholder(createdBody.invite_token);

    const createdReplay = corpusDocument('membership-create-replay');
    expect([createdReplay.operation_id, createdReplay.status, createdReplay.variant]).toEqual([
      'createMembership',
      201,
      'replay',
    ]);
    const createdReplayBody = createdReplay.body as { invite_token: string | null };
    // A replay is the stored answer: the same membership, the token withheld — never a second one.
    expect(createdReplayBody.invite_token).toBeNull();
    expect(withoutInviteToken(createdReplayBody)).toEqual(withoutInviteToken(createdBody));

    // 2. `resendMembershipInvite`'s 202 rotates the pending row's token: same row, new `updated_at`
    //    and version, so the tab can prove it kept the membership and only reissued the link.
    const resent = corpusDocument('membership-resend-ok');
    expect([resent.operation_id, resent.status, resent.variant]).toEqual([
      'resendMembershipInvite',
      202,
      'ok',
    ]);
    const resentBody = resent.body as {
      id: string;
      status: string;
      version: number;
      invite_token: string | null;
    };
    expect(resentBody.id).toBe(pendingRow?.id);
    expect(resentBody.status).toBe('pending');
    expect(resentBody.version).toBe(2);
    expectInvitePlaceholder(resentBody.invite_token);
    // The rotation mints a different token from the create: one value cannot stand for both calls.
    expect(resentBody.invite_token).not.toBe(createdBody.invite_token);

    const resentReplay = corpusDocument('membership-resend-replay');
    expect([resentReplay.operation_id, resentReplay.status, resentReplay.variant]).toEqual([
      'resendMembershipInvite',
      202,
      'replay',
    ]);
    const resentReplayBody = resentReplay.body as { invite_token: string | null };
    expect(resentReplayBody.invite_token).toBeNull();
    expect(withoutInviteToken(resentReplayBody)).toEqual(withoutInviteToken(resentBody));

    // 3. `cancelMembershipInvite`'s answer is bodyless, like `removeMember`'s.
    const cancelled = corpusDocument('membership-cancel-ok');
    expect([cancelled.operation_id, cancelled.status, cancelled.variant]).toEqual([
      'cancelMembershipInvite',
      204,
      'ok',
    ]);
    expect(cancelled.body, 'a 204 answer carries no body').toBeUndefined();

    // 4. Both lifecycle calls refuse a row that is not pending with the same 409, carrying the
    //    server's own code — the state the tab must report instead of a rotated or cancelled invite.
    for (const name of ['membership-resend-not-pending', 'membership-cancel-not-pending']) {
      const refusal = corpusDocument(name);
      expect([refusal.status, refusal.variant], `fixtures/web-api/${name}`).toEqual([
        409,
        'not-pending',
      ]);
      const envelope = refusal.body as { error: { code: string; retryable: boolean } };
      expect(envelope.error.code, `fixtures/web-api/${name}`).toBe('invite_not_pending');
      expect(envelope.error.retryable, `fixtures/web-api/${name}`).toBe(false);
    }
  });

  it('pins the rotation burst refusal and its bounded retry delay (09.7.1-T3 AC3)', () => {
    const refusal = corpusDocument('ingest-token-rotate-rate-limited');
    expect([refusal.operation_id, refusal.status, refusal.variant]).toEqual([
      'rotateIngestToken',
      429,
      'rate-limited',
    ]);
    expect(refusal.headers['Retry-After']).toBe('30');
    const body = refusal.body as { error: { code: string; retryable: boolean } };
    expect(body.error.code).toBe('rate_limited');
    expect(body.error.retryable).toBe(true);
    expect(oneTimeSecretMembers(refusal.body)).toEqual([]);
  });
});
