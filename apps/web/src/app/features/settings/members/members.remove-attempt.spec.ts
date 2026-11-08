// Why this file exists: a removal's published state belongs to its attempt and to the org on
// screen. The cancel (and resend) dialogs reset every mutation state when they open, so a removal
// that is still on the wire can answer after a NEWER removal started — its answer must not clear
// that attempt's submitting state, and a reset removal must settle into nothing while still
// reloading the list, because the removal landed server-side either way. A removal that answers
// after the scope switched belongs to an org nobody shows: it publishes nothing.

import { TestBed } from '@angular/core/testing';
import type {
  MembershipListResponse,
  MembershipView,
  PermissionView,
  RoleView,
} from '@evidgate/sdk';

import { MembersDataService } from '../../../core/data/members-data.service';
import { ApiRequestError } from '../../../core/http/api-error';
import { createIntent } from '../../../core/http/intent';
import { MembersStore } from './members.store';

const ORG = '018f0000-0000-7000-8000-000000000001';
const OTHER = '018f0000-0000-7000-8000-000000000002';

/** One pending membership row: enough shape for the list, nothing the store derives from. */
function member(id: string, email: string): MembershipView {
  return {
    id,
    org_id: ORG,
    user_id: null,
    invited_email: email,
    status: 'pending',
    roles: [],
    created_at: '2026-10-01T00:00:00Z',
    updated_at: '2026-10-01T00:00:00Z',
    version: 1,
  };
}

/** `409 last_admin`, as `ApiGateway` builds it from the server envelope. */
function lastAdmin(): ApiRequestError {
  return new ApiRequestError({
    kind: 'conflict',
    status: 409,
    code: 'last_admin',
    message: 'the organization would be left without an administrator',
    requestId: 'req-remove-1',
    retryable: false,
    details: [],
    retryAfterSeconds: null,
  });
}

/** The reads and the one write these specs need; `manual` holds every removal until settled. */
class FakeMembersDataService {
  readonly page: MembershipListResponse = {
    items: [member('m-1', 'one@acme.test'), member('m-2', 'two@acme.test')],
    next_cursor: null,
  };
  readonly removals: string[] = [];
  readonly pending: Array<{ resolve: () => void; reject: (error: unknown) => void }> = [];
  manual = false;
  membershipCalls = 0;

  listMemberships(): Promise<MembershipListResponse> {
    this.membershipCalls += 1;
    return Promise.resolve(this.page);
  }

  listRoles(): Promise<readonly RoleView[]> {
    return Promise.resolve([]);
  }

  listPermissions(): Promise<readonly PermissionView[]> {
    return Promise.resolve([]);
  }

  remove(_orgId: string, membershipId: string): Promise<void> {
    this.removals.push(membershipId);
    if (!this.manual) {
      return Promise.resolve();
    }
    return new Promise<void>((resolve, reject) => {
      this.pending.push({ resolve, reject });
    });
  }

  settle(index: number): void {
    this.pending[index]?.resolve();
  }

  fail(index: number, error: unknown): void {
    this.pending[index]?.reject(error);
  }
}

describe('MembersStore removal attempt ownership', () => {
  let data: FakeMembersDataService;
  let store: MembersStore;

  beforeEach(() => {
    data = new FakeMembersDataService();
    TestBed.configureTestingModule({
      providers: [MembersStore, { provide: MembersDataService, useValue: data }],
    });
    store = TestBed.inject(MembersStore);
  });

  afterEach(() => {
    TestBed.resetTestingModule();
  });

  it('keeps a newer removal submitting when an older answer returns for the same org', async () => {
    await store.load(ORG);
    data.manual = true;
    const first = store.remove('m-1', createIntent());
    // A cancel or resend dialog opening while the removal is still out resets every state.
    store.resetMutations();
    const second = store.remove('m-2', createIntent());
    expect(store.removeState().phase).toBe('submitting');

    data.fail(0, lastAdmin());
    await expect(first).resolves.toBe(false);
    const stillSubmitting = store.removeState().phase;

    data.settle(1);
    await expect(second).resolves.toBe(true);
    expect(stillSubmitting).toBe('submitting');
    expect(store.removeState().phase).toBe('done');
    expect(data.removals).toEqual(['m-1', 'm-2']);
  });

  it('drops a removal answer that lands after the scope switched to another org', async () => {
    await store.load(ORG);
    data.manual = true;
    const removal = store.remove('m-1', createIntent());

    await store.load(OTHER);
    const phaseWhileAIsStillOut = store.removeState().phase;

    data.fail(0, lastAdmin());
    await expect(removal).resolves.toBe(false);
    // Org A's refusal is not org B's news: it publishes nothing and leaves no state behind.
    expect(store.removeState()).toEqual({ phase: 'idle', error: null });
    // Nor is org B busy with org A's removal, so its invite and edit dialogs open.
    expect(phaseWhileAIsStillOut).toBe('idle');

    // The scope's own removal still publishes: the state is idle, not stuck.
    data.manual = false;
    await expect(store.remove('m-2', createIntent())).resolves.toBe(true);
    expect(store.removeState().phase).toBe('done');
  });

  it('settles a removal whose dialog was reset into idle, and still reloads the list', async () => {
    await store.load(ORG);
    data.manual = true;
    const removal = store.remove('m-1', createIntent());

    store.resetMutations();
    data.settle(0);

    await expect(removal).resolves.toBe(true);
    expect(store.removeState().phase).toBe('idle');
    // The removal landed, so the list owes a reload even though its state was reset.
    expect(data.membershipCalls).toBe(2);
  });

  it('keeps a newer removal submitting across an A -> B -> A scope round trip', async () => {
    await store.load(ORG);
    data.manual = true;
    const first = store.remove('m-1', createIntent());
    store.resetMutations();

    await store.load(OTHER);
    await store.load(ORG);
    const second = store.remove('m-2', createIntent());
    expect(store.removeState().phase).toBe('submitting');

    // The older round trip answers after the scope came back to A, so its org still matches.
    data.settle(0);
    await expect(first).resolves.toBe(true);
    const stillSubmitting = store.removeState().phase;

    data.fail(1, lastAdmin());
    await expect(second).resolves.toBe(false);
    expect(stillSubmitting).toBe('submitting');
    expect(store.removeState().error?.code).toBe('last_admin');
    expect(data.removals).toEqual(['m-1', 'm-2']);
  });
});
