import { signal } from '@angular/core';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import { vi } from 'vitest';
import type { ApiError } from '../../../core/http/api-error';
import type { Intent } from '../../../core/http/intent';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import type { RolePreview } from './role-preview';
import type { RoleChange, IssuedInvite } from './members.store';

/** Mutation happy paths opt into desktop; responsive denial specs supply their own policy. */
export const MEMBERS_DESKTOP = {
  provide: NarrowMutationPolicy,
  useValue: { canMutate: () => true, narrow: () => false, allowsMutation: () => true },
};

export const role: RoleView = {
  id: 'role1',
  code: 'tenant_admin',
  name: 'Organization administrator',
  is_builtin: true,
  created_at: '2026-10-25T00:00:00Z',
  version: 1,
};
function member(id: string, status: MembershipView['status'] = 'active'): MembershipView {
  return {
    id,
    org_id: 'org1',
    invited_email: `${id}@example.test`,
    status,
    roles: [role],
    created_at: '2026-10-25T00:00:00Z',
    updated_at: '2026-10-25T00:00:00Z',
    version: 1,
  };
}
export const lastAdmin: ApiError = {
  kind: 'conflict',
  status: 409,
  code: 'last_admin',
  message: 'The last tenant administrator cannot be removed. <strong>Keep this text.</strong>',
  requestId: 'request1',
  retryable: false,
  details: [],
  retryAfterSeconds: null,
};
type Mutation = { phase: 'idle' | 'submitting' | 'failed' | 'done'; error: ApiError | null };

/** Deliberately independent of the real store's implementation. */
export class FakeMembersStore {
  readonly members = signal<readonly MembershipView[]>([
    member('owner'),
    member('pending', 'pending'),
    member('removed', 'removed'),
  ]);
  readonly roles = signal<readonly RoleView[]>([role]);
  readonly catalogue = signal(null);
  readonly phase = signal<'loading' | 'ready' | 'error'>('ready');
  readonly loadError = signal<ApiError | null>(null);
  readonly hasMore = signal(false);
  readonly inviteState = signal<Mutation>({ phase: 'idle', error: null });
  readonly removeState = signal<Mutation>({ phase: 'idle', error: null });
  readonly editState = signal<Mutation>({ phase: 'idle', error: null });
  readonly resendState = signal<Mutation>({ phase: 'idle', error: null });
  readonly cancelState = signal<Mutation>({ phase: 'idle', error: null });
  readonly resend = vi.fn<(id: string, intent: Intent) => Promise<IssuedInvite | null>>(() =>
    Promise.resolve({ membershipId: 'pending', inviteToken: 'page-resend-once' }),
  );
  readonly cancel = vi.fn<(id: string, intent: Intent) => Promise<boolean>>(() =>
    Promise.resolve(true),
  );
  readonly editApplied = signal<readonly Pick<RoleChange, 'kind' | 'roleId'>[]>([]);
  readonly changeRoles = vi.fn<(id: string, changes: readonly RoleChange[]) => Promise<boolean>>(
    () => Promise.resolve(true),
  );
  readonly memberById = (id: string) => this.members().find((value) => value.id === id);
  readonly load = vi.fn(() => Promise.resolve());
  readonly loadMore = vi.fn(() => Promise.resolve());
  readonly invite = vi.fn<
    (email: string, roleIds: readonly string[], intent: Intent) => Promise<IssuedInvite | null>
  >(() => Promise.resolve({ membershipId: 'new', inviteToken: 'page-create-once' }));
  readonly remove = vi.fn<(id: string, intent: Intent) => Promise<boolean>>(() =>
    Promise.resolve(true),
  );
  readonly matrix = () => ({
    columns: this.roles(),
    rows: this.members().map((membership) => ({
      membership,
      granted: new Set(membership.roles.map((value) => value.id)),
    })),
  });
  readonly preview = vi.fn((id: string): RolePreview =>
    id === role.id
      ? {
          kind: 'ready',
          role,
          omitted: 1,
          groups: [
            {
              resource: 'membership',
              permissions: [
                { code: 'membership:read', description: 'Read members', sensitive: true },
              ],
            },
          ],
        }
      : { kind: 'unavailable', reason: 'unknown-role' },
  );
  resetMutations(): void {
    this.inviteState.set({ phase: 'idle', error: null });
    this.removeState.set({ phase: 'idle', error: null });
    this.editState.set({ phase: 'idle', error: null });
    this.resendState.set({ phase: 'idle', error: null });
    this.cancelState.set({ phase: 'idle', error: null });
    this.editApplied.set([]);
  }
}
