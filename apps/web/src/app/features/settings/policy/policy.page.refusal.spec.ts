import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRoute, convertToParamMap } from '@angular/router';
import { SessionStore } from '../../../core/session/session.store';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { PolicyPage } from './policy.page';
import { FakePolicyStore, PolicyOverviewHarness, revision } from './policy.page.test-support';
import { PolicyStore } from './policy.store';

provideLayoutTestStubs();

describe('PolicyPage refusal boundaries', () => {
  let store: FakePolicyStore;
  const desktop = signal(true);
  const currentOrg = signal<{ id: string } | null>({ id: 'org' });
  const currentProject = signal<{ id: string } | null>(null);
  const projects = signal([{ id: 'project' }]);
  beforeEach(() => {
    store = new FakePolicyStore();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: ActivatedRoute,
          useValue: { snapshot: { queryParamMap: convertToParamMap({}) } },
        },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: desktop,
            narrow: computed(() => !desktop()),
            allowsMutation: () => desktop(),
          },
        },
        {
          provide: SessionStore,
          useValue: { currentOrg, currentProject, projects, has: () => true },
        },
      ],
    });
    TestBed.overrideComponent(PolicyPage, {
      set: { providers: [{ provide: PolicyStore, useValue: store }] },
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
  });
  async function render() {
    const fixture = TestBed.createComponent(PolicyPage);
    fixture.detectChanges();
    await fixture.whenStable();
    return { fixture };
  }
  it('closes a viewed revision on refusal and does not reopen it after recovery', async () => {
    const { fixture } = await render();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      PolicyOverviewHarness,
    );
    await (await harness.viewRevision()).click();
    expect(await harness.optionalViewDialog()).not.toBeNull();
    store.revisions.set([]);
    store.openedRevision.set(null);
    store.phase.set('not_found');
    expect(await harness.optionalViewDialog()).toBeNull();
    store.revisions.set([revision]);
    store.phase.set('ready');
    expect(await harness.optionalViewDialog()).toBeNull();
    await (await harness.viewRevision()).click();
    expect(await harness.optionalViewDialog()).not.toBeNull();
  });
  it.each([
    { kind: 'forbidden', status: 403 },
    { kind: 'unauthenticated', status: 401 },
    { kind: 'not_found', status: 404 },
  ] as const)('hides $kind details behind not-found copy', async ({ kind, status }) => {
    store.phase.set('not_found');
    store.revisions.set([]);
    store.loadError.set({
      kind,
      status,
      code: `distinctive_${kind}_code`,
      message: `Distinctive ${kind} refusal message`,
      requestId: 'distinctive-refusal-request',
      retryable: false,
      details: [{ path: 'distinctive-refusal-path', reason: 'distinctive-refusal-reason' }],
      retryAfterSeconds: null,
    });
    const { fixture } = await render();
    const harness = await TestbedHarnessEnvironment.harnessForFixture(
      fixture,
      PolicyOverviewHarness,
    );
    const text = await (await harness.host()).text();
    expect(text).toContain('Revision not found.');
    expect(text).not.toContain('distinctive_');
    expect(text).not.toContain('Distinctive');
    expect(text).not.toContain('distinctive-refusal');
    expect(store.loadError()?.kind).toBe(kind);
  });
});
