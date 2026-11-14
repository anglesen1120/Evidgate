import { Component, provideZonelessChangeDetection, signal } from '@angular/core';
import { ComponentFixture, TestBed } from '@angular/core/testing';
import type { PolicyActivationStateDto } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { DEFAULT_ADVISORY_POLICY } from './default-policy';
import { EnableCollectionPage } from './enable-collection.page';
import {
  ACTIVATION,
  ACTIVATION_STATE,
  REVISION,
  type ReadActivation,
  confirmButton,
  deferredResult,
  drain,
  failure,
  retryButton,
  submit,
} from './enable-collection.page.test-support';

@Component({
  selector: 'app-enable-collection-host',
  imports: [EnableCollectionPage],
  template: `<app-onboarding-enable-collection [orgId]="orgId()" [projectId]="projectId()" />`,
})
class EnableCollectionHost {
  readonly orgId = signal('org-1');
  readonly projectId = signal('project-1');
}

describe('EnableCollectionPage scope changes', () => {
  const permitted = signal(true);
  const has = vi.fn((code: string) => permitted() && code === 'policy:write');
  const createPolicyRevision = vi.fn<OnboardingDataService['createPolicyRevision']>();
  const activatePolicy = vi.fn<OnboardingDataService['activatePolicy']>();
  const readPolicyActivation = vi.fn<ReadActivation>();
  let host: ComponentFixture<EnableCollectionHost>;
  let hostRoot: HTMLElement;

  function press(): void {
    submit(hostRoot);
  }

  async function drainHost(): Promise<void> {
    await drain(host);
  }

  beforeEach(async () => {
    permitted.set(true);
    createPolicyRevision.mockReset().mockResolvedValue(REVISION);
    activatePolicy.mockReset().mockResolvedValue(ACTIVATION);
    readPolicyActivation.mockReset().mockResolvedValue(null);
    TestBed.resetTestingModule();
    await TestBed.configureTestingModule({
      imports: [EnableCollectionHost],
      providers: [
        provideZonelessChangeDetection(),
        {
          provide: OnboardingDataService,
          useValue: { createPolicyRevision, activatePolicy, readPolicyActivation },
        },
        { provide: SessionStore, useValue: { has } },
      ],
    }).compileComponents();
    host = TestBed.createComponent(EnableCollectionHost);
    hostRoot = host.nativeElement as HTMLElement;
    await drain(host);
  });

  it('clears the appended revision and the intent when the scope moves', async () => {
    press();
    await drainHost();
    expect(hostRoot.querySelector('[data-testid="enable-collection-enabled"]')).not.toBeNull();
    const firstKey = activatePolicy.mock.calls[0]![2].idempotencyKey;

    // The wizard moved to another project: the terminal panel describes an answer for the old one.
    host.componentInstance.projectId.set('project-2');
    await drainHost();
    expect(hostRoot.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    expect(confirmButton(hostRoot)).not.toBeNull();

    press();
    await drainHost();
    // A new intent for the new scope: new key, and the cached revision of the old intent is gone.
    expect(createPolicyRevision).toHaveBeenCalledTimes(2);
    expect(createPolicyRevision).toHaveBeenLastCalledWith('project-2', DEFAULT_ADVISORY_POLICY);
    expect(activatePolicy).toHaveBeenCalledTimes(2);
    expect(activatePolicy.mock.calls[1]![2].idempotencyKey).not.toBe(firstKey);
  });

  it('re-reads the activation when the scope moves and shows what the new read answers', async () => {
    // The first read (the host's initial project) already answered 404 during setup.
    readPolicyActivation
      .mockReset()
      .mockResolvedValue({ ...ACTIVATION_STATE, project_id: 'project-2' });
    host.componentInstance.projectId.set('project-2');
    await drainHost();

    expect(readPolicyActivation).toHaveBeenCalledExactlyOnceWith('project-2');
    const enabled = hostRoot.querySelector('[data-testid="enable-collection-enabled"]');
    expect(enabled).not.toBeNull();
    expect(enabled!.textContent).toContain('project-2');
    // The new scope's activation is read-only: no confirm and no command.
    expect(confirmButton(hostRoot)).toBeNull();
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });

  it('re-reads on an org change and ignores the late answer of the previous scope', async () => {
    // The setup read for org-1 already answered 404; this test owns the next two reads.
    const deferred = deferredResult<PolicyActivationStateDto | null>();
    readPolicyActivation
      .mockReset()
      .mockImplementationOnce(() => deferred.promise)
      .mockResolvedValueOnce(null);

    // The org moved while this read was in flight; the project id did not.
    host.componentInstance.orgId.set('org-2');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledTimes(1);

    // …and it moved again, so the answer deferred for org-2 is stale by the time it arrives.
    host.componentInstance.orgId.set('org-3');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledTimes(2);
    expect(readPolicyActivation).toHaveBeenLastCalledWith('project-1');

    deferred.resolve(ACTIVATION_STATE);
    await drainHost();

    // The stale answer belongs to org-1 and must not repaint this page.
    expect(hostRoot.querySelector('[data-testid="enable-collection-enabled"]')).toBeNull();
    expect(confirmButton(hostRoot)).not.toBeNull();
    expect(confirmButton(hostRoot)!.disabled).toBe(false);
  });

  it('drops the late answer of the project a project change left behind', async () => {
    // The setup read for project-1 already answered 404; this test owns the next two reads.
    const deferred = deferredResult<PolicyActivationStateDto | null>();
    readPolicyActivation
      .mockReset()
      .mockResolvedValue(null)
      .mockImplementationOnce(() => deferred.promise)
      .mockResolvedValueOnce({ ...ACTIVATION_STATE, project_id: 'project-2' });

    // An org move starts a read for project-1 that is still in flight when the project moves.
    host.componentInstance.orgId.set('org-2');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledTimes(1);
    expect(readPolicyActivation).toHaveBeenLastCalledWith('project-1');

    host.componentInstance.projectId.set('project-2');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledTimes(2);
    expect(readPolicyActivation).toHaveBeenLastCalledWith('project-2');

    // The deferred answer belongs to project-1, and the page now shows project-2's.
    deferred.resolve(ACTIVATION_STATE);
    await drainHost();

    const enabled = hostRoot.querySelector('[data-testid="enable-collection-enabled"]');
    expect(enabled).not.toBeNull();
    expect(enabled!.textContent).toContain('project-2');
    expect(enabled!.textContent).not.toContain('project-1');
    expect(confirmButton(hostRoot)).toBeNull();
  });

  it('keeps a lost re-read’s focus request out of the next scope’s failure', async () => {
    // A read the user pressed Retry for, left in flight while the scope moves: its focus request
    // belongs to a press that no longer exists, so the new scope's *first* failure — an answer to
    // nobody's press — must leave focus where it is.
    readPolicyActivation.mockReset().mockRejectedValue(failure('unavailable', 503));
    host.componentInstance.projectId.set('project-2');
    await drainHost();
    const heading = hostRoot.querySelector<HTMLElement>('#enable-read-failed-heading');
    expect(heading).not.toBeNull();
    expect(document.activeElement).not.toBe(heading);

    const inFlight = deferredResult<PolicyActivationStateDto | null>();
    readPolicyActivation
      .mockReset()
      .mockResolvedValue(null)
      .mockImplementationOnce(() => inFlight.promise)
      .mockRejectedValueOnce(failure('unavailable', 503));

    retryButton(hostRoot)!.click();
    await drainHost();
    expect(readPolicyActivation).toHaveBeenCalledExactlyOnceWith('project-2');

    // The scope moves before that answer arrives; the new scope's own read fails, and only then does
    // the answer the lost generation was waiting for arrive.
    host.componentInstance.projectId.set('project-3');
    await drainHost();
    expect(readPolicyActivation).toHaveBeenLastCalledWith('project-3');
    inFlight.resolve(null);
    await drainHost();

    const latest = hostRoot.querySelector<HTMLElement>('#enable-read-failed-heading');
    expect(latest).not.toBeNull();
    expect(document.activeElement).not.toBe(latest);
    expect(
      hostRoot.contains(document.activeElement),
      'a failure nobody pressed for took focus',
    ).toBe(false);
    expect(createPolicyRevision).not.toHaveBeenCalled();
    expect(activatePolicy).not.toHaveBeenCalled();
  });
});
