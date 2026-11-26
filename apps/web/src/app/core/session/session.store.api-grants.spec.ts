// The real `ApiPermissionsSource` under the real `SessionStore`: grants are per org, so a slow
// answer for org A that lands after the switch to org B must not be published, and an unreadable
// grant set must grant nothing.

import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import type { MyPermissionsResponse } from '@evidgate/sdk';
import { vi } from 'vitest';

import { IdentityDataService } from '../data/identity-data.service';
import { SESSION_ENDER } from './session-ender';
import { SessionStore } from './session.store';
import {
  FakeIdentityDataService,
  FakeSessionEnder,
  ORG_ONE,
  ORG_TWO,
  PROJECT_P1,
  PROJECT_Q1,
  settle,
} from './session.store.test-support';

interface Pending {
  readonly orgId: string;
  readonly resolve: (answer: MyPermissionsResponse) => void;
  readonly reject: (error: unknown) => void;
}

class GrantsIdentity extends FakeIdentityDataService {
  manual = false;
  failure: Error | null = null;
  readonly answers = new Map<string, readonly string[]>();
  readonly pendingGrants: Pending[] = [];

  getMyPermissions(orgId: string): Promise<MyPermissionsResponse> {
    if (this.manual) {
      return new Promise((resolve, reject) => {
        this.pendingGrants.push({ orgId, resolve, reject });
      });
    }
    if (this.failure !== null) return Promise.reject(this.failure);
    return Promise.resolve(grants(orgId, this.answers.get(orgId) ?? []));
  }
}

function grants(orgId: string, permissions: readonly string[]): MyPermissionsResponse {
  return { org_id: orgId, roles: [], permissions: [...permissions] };
}

describe('SessionStore with the API permission source', () => {
  let identity: GrantsIdentity;
  let store: SessionStore;

  beforeEach(() => {
    identity = new GrantsIdentity();
    identity.organizations = [ORG_ONE, ORG_TWO];
    identity.projectsByOrg.set('o-1', [PROJECT_P1]);
    identity.projectsByOrg.set('o-2', [PROJECT_Q1]);
    identity.answers.set('o-1', ['audit:read']);
    identity.answers.set('o-2', ['run:read']);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: IdentityDataService, useValue: identity },
        { provide: SESSION_ENDER, useValue: new FakeSessionEnder() },
      ],
    });
    store = TestBed.inject(SessionStore);
    vi.spyOn(TestBed.inject(Router), 'navigate').mockResolvedValue(true);
  });

  it('publishes the grants of the org the session opened in, then re-reads on a switch', async () => {
    await store.bootstrap();
    expect(store.has('audit:read')).toBe(true);

    expect(await store.resolveScope('o-2', null)).toBe('ok');

    expect(store.has('run:read')).toBe(true);
    expect(store.has('audit:read')).toBe(false);
  });

  it('drops a slow answer for org A that arrives after the switch to org B', async () => {
    await store.bootstrap();
    await store.resolveScope('o-1', null);
    identity.manual = true;

    const toA = store.resolveScope('o-2', null);
    await settle();
    // The user moves on before org 2's answer arrives: back to org 1 is the newer resolution.
    const toB = store.resolveScope('o-1', null);
    await settle();
    expect(identity.pendingGrants.map((read) => read.orgId)).toEqual(['o-2', 'o-1']);

    identity.pendingGrants[1]?.resolve(grants('o-1', ['audit:read']));
    expect(await toB).toBe('ok');
    identity.pendingGrants[0]?.resolve(grants('o-2', ['run:read', 'policy:write']));

    expect(await toA).toBe('superseded');
    expect(store.has('audit:read')).toBe(true);
    expect(store.has('run:read')).toBe(false);
    expect(store.has('policy:write')).toBe(false);
  });

  it('grants nothing and reports unavailable when the grants cannot be read', async () => {
    identity.failure = new Error('grants unavailable');

    await store.bootstrap();

    expect(store.sessionState()).toBe('unavailable');
    expect(store.has('audit:read')).toBe(false);
  });
});
