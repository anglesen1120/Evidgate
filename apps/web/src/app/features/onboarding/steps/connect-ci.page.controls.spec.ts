import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection, signal } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { IngestTokenView } from '@evidgate/sdk';

import { OnboardingDataService } from '../../../core/data/onboarding-data.service';
import { SessionStore } from '../../../core/session/session.store';
import { OnboardingStore } from '../onboarding.store';
import { OnboardingViewport } from '../onboarding-viewport.testing';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { ConnectCiPage } from './connect-ci.page';
import { requiredElement } from './step-controls.testing';
import { TOKEN, provideTokenInventory } from './connect-ci.page.test-support';

class CiControlsHarness extends ComponentHarness {
  static hostSelector = 'app-onboarding-connect-ci';
  readonly requiredFields = this.locatorForAll('form [required]');
  readonly fieldErrors = this.locatorForAll('app-field-feedback [id$="-error"]');
  readonly tokenPrefix = this.locatorFor('[data-testid="connect-token-row"] dl dd code');

  readonly factLabels = this.locatorForAll(
    '[data-testid="connect-token-row"] > th[scope="row"] dl dt',
  );
}

describe('ConnectCiPage Helm controls', () => {
  let fixture: ComponentFixture<ConnectCiPage>;
  let root: HTMLElement;
  let viewport: OnboardingViewport;
  const tokens = signal<readonly IngestTokenView[]>([]);
  const createIngestToken = vi.fn<OnboardingDataService['createIngestToken']>();
  const rotateIngestToken = vi.fn<OnboardingDataService['rotateIngestToken']>();
  const permitted = signal(true);
  const refreshFailure = signal<'unavailable' | null>(null);
  const refresh = vi.fn<() => Promise<void>>();

  beforeEach(async () => {
    permitted.set(true);
    viewport = new OnboardingViewport();
    tokens.set([]);
    createIngestToken.mockReset();
    rotateIngestToken.mockReset();
    refreshFailure.set(null);
    refresh.mockReset().mockResolvedValue(undefined);
    provideTokenInventory({ tokens, tokensRefreshFailure: refreshFailure, refreshTokens: refresh });
    await TestBed.configureTestingModule({
      imports: [ConnectCiPage],
      providers: [
        provideZonelessChangeDetection(),
        viewport.provider,
        {
          provide: OnboardingDataService,
          useValue: { createIngestToken, rotateIngestToken },
        },
        {
          provide: OnboardingStore,
          useValue: {
            project: signal({ slug: 'storefront-web' }),
          },
        },
        {
          provide: SessionStore,
          useValue: { has: () => permitted(), onSessionEnd: () => () => undefined },
        },
      ],
    }).compileComponents();
    fixture = TestBed.createComponent(ConnectCiPage);
    fixture.componentRef.setInput('orgId', 'org-1');
    fixture.componentRef.setInput('projectId', 'project-1');
    root = fixture.nativeElement as HTMLElement;
    await fixture.whenStable();
  });

  it('names the empty token panel and offers only one credential form', () => {
    expect(root.querySelectorAll('form')).toHaveLength(1);
    const panel = requiredElement<HTMLElement>(root, '[data-testid="connect-token-panel"]');
    expect(panel.getAttribute('data-slot')).toBe('card');
    expect(panel.getAttribute('aria-labelledby')).toBe('connect-token-heading');
    const table = requiredElement<HTMLTableElement>(panel, 'table');
    expect(requiredElement(table, 'caption').textContent).toContain('Ingest tokens');
    expect(table.querySelectorAll('tbody > tr')).toHaveLength(1);
    expect(requiredElement(table, 'tbody td').textContent?.trim()).toBe('No ingest tokens yet.');
    expect(table.querySelector('button')).toBeNull();
    expect(requiredElement(root, '[data-testid="connect-token-submit"]').classList).toContain(
      'spartan-button-variant-outline',
    );
  });

  it('preserves native keyboard stops and labels on every Helm field', () => {
    const fields = root.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>('[hlmInput]');
    expect(fields).toHaveLength(1);
    for (const field of fields) {
      expect(field.getAttribute('data-slot')).toBe('input');
      expect(field.labels?.item(0)?.htmlFor).toBe(field.id);
      expect(field.required).toBe(true);
      expect(field.labels?.item(0)?.textContent).not.toContain('*');
      const hintId = field.getAttribute('aria-describedby');
      expect(hintId).not.toBeNull();
      expect(root.querySelector(`#${hintId}`)?.textContent?.trim()).not.toBe('');
      field.focus();
      expect(document.activeElement).toBe(field);
      expect(field.tabIndex).toBe(0);
    }
  });

  it('shows only hints on the untouched required control before submission', async () => {
    const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, CiControlsHarness);
    const fields = await page.requiredFields();
    expect(fields).toHaveLength(1);
    expect(await page.fieldErrors()).toHaveLength(0);
    for (const field of fields) {
      expect(await field.getAttribute('aria-invalid')).not.toBe('true');
      expect(await field.getAttribute('data-matches-spartan-invalid')).not.toBe('true');
      expect(await field.getAttribute('aria-describedby')).toBe(
        `${await field.getAttribute('id')}-hint`,
      );
    }
  });

  it.each([390, 1440])(
    'keeps the labelled token prefix in the record rather than squeezed table columns at %ipx',
    async (width) => {
      viewport.setWidth(width);
      tokens.set([TOKEN]);
      const page = await TestbedHarnessEnvironment.harnessForFixture(fixture, CiControlsHarness);
      const labels = await page.factLabels();
      expect(await Promise.all(labels.map((term) => term.text()))).toEqual(['Status', 'Prefix']);
      const prefix = await page.tokenPrefix();
      expect(await prefix.text()).toBe(TOKEN.prefix);
      expect(await prefix.hasClass('whitespace-nowrap')).toBe(true);
    },
  );

  it('explains required fields once for the token form', () => {
    expect(root.textContent?.match(/All fields required unless marked optional\./g)).toHaveLength(
      1,
    );
    expect(root.querySelectorAll('label [aria-hidden="true"]')).toHaveLength(0);
  });

  it('keeps field validation visible to Brain and names the error after form submission', async () => {
    requiredElement<HTMLFormElement>(
      root,
      '[data-testid="connect-token-panel"] form',
    ).requestSubmit();
    await fixture.whenStable();
    const field = requiredElement(root, '#connect-token-name');
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(field.getAttribute('data-matches-spartan-invalid')).toBe('true');
    expect(field.getAttribute('aria-describedby')).toBe(
      'connect-token-name-hint connect-token-name-error',
    );
    expect(createIngestToken).not.toHaveBeenCalled();
  });

  it('retains focus on the aria-disabled refresh control while refusing duplicate requests', async () => {
    refreshFailure.set('unavailable');
    refresh.mockImplementation(() => new Promise(() => undefined));
    await fixture.whenStable();
    const button = requiredElement<HTMLButtonElement>(
      root,
      '[data-testid="connect-ci-refresh-failed"] button',
    );
    button.focus();
    button.click();
    await fixture.whenStable();
    expect(button.getAttribute('data-slot')).toBe('button');
    expect(button.disabled).toBe(false);
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(document.activeElement).toBe(button);
    button.click();
    expect(refresh).toHaveBeenCalledTimes(1);
  });

  it('keeps all mutation controls natively disabled when permission is absent', async () => {
    permitted.set(false);
    await fixture.whenStable();
    for (const field of root.querySelectorAll<HTMLInputElement | HTMLTextAreaElement>(
      '[hlmInput]',
    )) {
      expect(field.disabled).toBe(true);
    }
    expect(
      requiredElement<HTMLButtonElement>(root, '[data-testid="connect-token-submit"]').disabled,
    ).toBe(true);
  });
  it('shows a touched correction on blur without submitting or moving focus', async () => {
    const field = requiredElement<HTMLInputElement>(root, '#connect-token-name');
    field.focus();
    field.dispatchEvent(new Event('blur'));
    await fixture.whenStable();
    expect(field.getAttribute('aria-invalid')).toBe('true');
    expect(field.getAttribute('data-matches-spartan-invalid')).toBe('true');
    expect(field.getAttribute('aria-describedby')).toContain('connect-token-name-error');
    field.value = '123';
    field.dispatchEvent(new Event('input', { bubbles: true }));
    await fixture.whenStable();
    expect(field.getAttribute('aria-invalid')).not.toBe('true');
    expect(field.getAttribute('data-matches-spartan-invalid')).not.toBe('true');
    expect(field.getAttribute('aria-describedby')).toBe('connect-token-name-hint');
    expect(root.querySelector('#connect-token-errors')).toBeNull();
    expect(document.activeElement).toBe(field);
    expect(createIngestToken).not.toHaveBeenCalled();
  });

  it.each([320, 390, 767])(
    'guards all CI mutations at %ipx while retaining drafts and existing reveal',
    async (width) => {
      tokens.set([TOKEN]);
      await fixture.whenStable();
      const values: Record<string, string> = {
        'connect-token-name': 'Retained token draft',
      };
      for (const [id, value] of Object.entries(values)) {
        const field = requiredElement<HTMLInputElement | HTMLTextAreaElement>(root, `#${id}`);
        field.value = value;
        field.dispatchEvent(new Event('input', { bubbles: true }));
      }
      await fixture.whenStable();
      const forms = Array.from(root.querySelectorAll<HTMLFormElement>('form'));
      const rotate = requiredElement<HTMLButtonElement>(
        root,
        '[data-testid="connect-token-rotate"]',
      );
      rotate.click();
      await fixture.whenStable();
      const confirm = requiredElement<HTMLButtonElement>(
        document.body,
        '[data-testid="connect-rotation-confirm"]',
      );
      fixture.debugElement.injector
        .get(RevealSlot)
        .show({ source: 'ingest-token', value: 'test-only-reveal' });
      viewport.setWidth(width);
      for (const form of forms) form.requestSubmit();
      confirm.click();
      await fixture.whenStable();
      expect(root.querySelector('form')).toBeNull();
      expect(root.querySelector('[data-testid="connect-token-rotate"]')).toBeNull();
      expect(root.querySelector('[data-testid="connect-token-draft"]')?.textContent).toContain(
        'Retained token draft',
      );
      expect(requiredElement<HTMLInputElement>(root, '[data-testid="reveal-value"]').value).toBe(
        'test-only-reveal',
      );
      expect(requiredElement<HTMLButtonElement>(root, '[data-testid="reveal-copy"]').disabled).toBe(
        false,
      );
      expect(root.querySelector('[data-testid="connection-health"]')).not.toBeNull();
      expect(createIngestToken).not.toHaveBeenCalled();
      expect(rotateIngestToken).not.toHaveBeenCalled();
      viewport.setWidth(768);
      await fixture.whenStable();
      expect(requiredElement<HTMLInputElement>(root, '#connect-token-name').value).toBe(
        'Retained token draft',
      );
    },
  );
});
