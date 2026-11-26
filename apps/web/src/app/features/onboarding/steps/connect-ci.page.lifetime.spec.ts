import { provideZonelessChangeDetection, signal } from '@angular/core';
import { TestBed, type ComponentFixture } from '@angular/core/testing';
import type { IngestTokenCreatedResponse } from '@evidgate/sdk';
import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { OnboardingStore } from '../onboarding.store';
import { provideDesktopOnboardingViewport } from '../onboarding-viewport.testing';
import { ConnectCiPage } from './connect-ci.page';
import { TOKEN, provideTokenInventory } from './connect-ci.page.test-support';
function required<T extends Element>(root: HTMLElement, selector: string): T {
  const result = root.querySelector<T>(selector);
  if (!result) throw new Error(`Missing ${selector}`);
  return result;
}

describe('ConnectCiPage command lifetime boundaries', () => {
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  const createIngestToken = vi.fn<OnboardingDataService['createIngestToken']>();
  const refresh = vi.fn<(orgId: string, projectId: string) => Promise<void>>();
  let endSession: () => void;
  let resolveCommand: (answer: IngestTokenCreatedResponse) => void;

  beforeEach(async () => {
    createIngestToken.mockReset().mockImplementation(
      () =>
        new Promise<IngestTokenCreatedResponse>((resolve) => {
          resolveCommand = resolve;
        }),
    );
    refresh.mockReset().mockResolvedValue(undefined);
    provideTokenInventory({ tokens: signal([]), refreshTokens: refresh });
    TestBed.configureTestingModule({
      imports: [ConnectCiPage],
      providers: [
        provideZonelessChangeDetection(),
        provideDesktopOnboardingViewport(),
        { provide: OnboardingDataService, useValue: { createIngestToken } },
        {
          provide: OnboardingStore,
          useValue: {
            project: signal({ slug: 'web' }),
          },
        },
        {
          provide: SessionStore,
          useValue: {
            has: () => true,
            onSessionEnd: (callback: () => void) => {
              endSession = callback;
              return () => undefined;
            },
          },
        },
      ],
    });
    fixture = TestBed.createComponent(ConnectCiPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    fixture.componentRef.setInput('active', true);
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });
  afterEach(() => TestBed.resetTestingModule());

  async function startCommand(): Promise<void> {
    const field = required<HTMLInputElement>(root, '#connect-token-name');
    field.value = 'Non-secret draft';
    field.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    required<HTMLButtonElement>(root, '[data-testid="connect-token-submit"]').click();
    await fixture.whenStable();
    expect(createIngestToken).toHaveBeenCalledTimes(1);
  }

  function expectNoReveal(): void {
    expect(fixture.debugElement.injector.get(RevealSlot).current()).toBeNull();
    expect(root.querySelector('[data-testid="reveal-value"]')).toBeNull();
  }

  it('reveals a successful answer only while its original active scope still exists', async () => {
    await startCommand();
    resolveCommand({ token: TOKEN, secret: 'synthetic-one-time-answer' });
    await vi.waitFor(() => {
      fixture.detectChanges();
      expect(fixture.debugElement.injector.get(RevealSlot).current()?.value).toBe(
        'synthetic-one-time-answer',
      );
    });
    expect(required<HTMLInputElement>(root, '[data-testid="reveal-value"]').value).toBe(
      'synthetic-one-time-answer',
    );
    fixture.componentRef.setInput('active', false);
    await fixture.whenStable();
    expectNoReveal();
  });

  it.each(['orgId', 'projectId'])(
    'ignores a late answer when %s changes during the command',
    async (input) => {
      await startCommand();
      fixture.componentRef.setInput(input, 'scope-2');
      await fixture.whenStable();
      resolveCommand({ token: TOKEN, secret: 'synthetic-late-answer' });
      await fixture.whenStable();
      fixture.detectChanges();
      expectNoReveal();
      expect(required<HTMLInputElement>(root, '#connect-token-name').value).toBe('');
      expect(refresh).not.toHaveBeenCalled();
    },
  );

  it('reconciles a committed token after re-entry without resurrecting its secret', async () => {
    await startCommand();
    fixture.componentRef.setInput('active', false);
    await fixture.whenStable();
    fixture.componentRef.setInput('active', true);
    await fixture.whenStable();
    resolveCommand({ token: TOKEN, secret: 'synthetic-late-answer' });
    await vi.waitFor(() => expect(refresh).toHaveBeenCalledExactlyOnceWith('org-1', 'project-1'));
    fixture.detectChanges();
    expectNoReveal();
    expect(required<HTMLInputElement>(root, '#connect-token-name').value).toBe('Non-secret draft');
  });

  it('does not reload the optional inventory or reveal a late answer while the step is hidden', async () => {
    await startCommand();
    fixture.componentRef.setInput('active', false);
    await fixture.whenStable();
    resolveCommand({ token: TOKEN, secret: 'synthetic-late-answer' });
    await fixture.whenStable();
    expect(refresh).not.toHaveBeenCalled();
    expectNoReveal();
    expect(root.querySelector('#connect-token-name')).toBeNull();
    fixture.componentRef.setInput('active', true);
    await fixture.whenStable();
    expect(required<HTMLInputElement>(root, '#connect-token-name').value).toBe('Non-secret draft');
  });

  it('leaves the old scope untouched when a late answer arrives after a real scope change', async () => {
    await startCommand();
    fixture.componentRef.setInput('projectId', 'project-2');
    await fixture.whenStable();
    resolveCommand({ token: TOKEN, secret: 'synthetic-late-answer' });
    await fixture.whenStable();
    expect(refresh).not.toHaveBeenCalled();
    expectNoReveal();
  });

  it('ignores a successful command answering after session end', async () => {
    await startCommand();
    endSession();
    resolveCommand({ token: TOKEN, secret: 'synthetic-late-answer' });
    await fixture.whenStable();
    fixture.detectChanges();
    expectNoReveal();
    expect(refresh).not.toHaveBeenCalled();
  });
});
