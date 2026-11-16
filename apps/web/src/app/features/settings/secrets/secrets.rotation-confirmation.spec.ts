import { InteractivityChecker } from '@angular/cdk/a11y';
import { computed, signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { NavigationStart, Router } from '@angular/router';
import { Subject } from 'rxjs';
import { SessionStore } from '../../../core/session/session.store';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { SecretsPage } from './secrets.page';
import { SecretsStore, type RotationResult } from './secrets.store';
import {
  base,
  FakeSecretsStore,
  renderSecretsPage,
  rotateButton,
  confirmRotation,
  chooseSecretProject,
} from './secrets-page.testing';

describe('Secret rotation explicit consent', () => {
  let store: FakeSecretsStore;
  const writable = signal(true);
  const wide = signal(true);
  let navigation: Subject<NavigationStart>;
  beforeEach(() => {
    store = new FakeSecretsStore();
    writable.set(true);
    wide.set(true);
    navigation = new Subject();
    TestBed.configureTestingModule({
      providers: [
        {
          provide: SessionStore,
          useValue: {
            projects: signal([
              { id: 'p1', name: 'Storefront' },
              { id: 'p2', name: 'API' },
            ]),
            has: (code: string) => code === 'ingest_token:write' && writable(),
          },
        },
        { provide: Router, useValue: { events: navigation } },
        {
          provide: NarrowMutationPolicy,
          useValue: {
            canMutate: wide,
            narrow: computed(() => !wide()),
            allowsMutation: () => wide(),
          },
        },
      ],
    });
    TestBed.overrideComponent(SecretsPage, {
      set: { providers: [{ provide: SecretsStore, useValue: store }, RevealSlot] },
    });
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.restoreAllMocks();
    navigation.complete();
  });

  it('names the complete secret, project, prefix, scopes and consequence before sending any request', async () => {
    const name = 'Tên bí mật sản xuất '.repeat(25) + '<img src=x onerror=bad()>';
    store.rows.set([{ ...base, name }]);
    const { root, fixture, slot } = await renderSecretsPage();
    const opener = rotateButton(root);
    opener.focus();
    opener.click();
    await fixture.whenStable();
    const dialog = document.querySelector('[role="alertdialog"]');
    expect(dialog?.textContent).toContain(name);
    expect(dialog?.textContent).toContain('Storefront');
    expect(dialog?.textContent).toContain(base.prefix);
    for (const scope of base.scopes) expect(dialog?.textContent).toContain(scope);
    expect(dialog?.textContent).toContain('previous credential stops working');
    expect(dialog?.textContent).toContain('does not update CI for you');
    expect(dialog?.querySelector('img')).toBeNull();
    expect(document.activeElement).toBe(dialog?.querySelector('[data-rotation-cancel]'));
    expect(store.rotate).not.toHaveBeenCalled();
    expect(slot.current()).toBeNull();
    await confirmRotation(fixture);
    expect(store.rotate).toHaveBeenCalledExactlyOnceWith(base.id);
    expect(document.querySelector('[role="alertdialog"]')).toBeNull();
    expect(document.querySelectorAll('[data-testid="reveal-value"]')).toHaveLength(1);
    expect(document.querySelectorAll('.cdk-focus-trap-anchor')).toHaveLength(2);
  });

  it.each(['cancel', 'escape'] as const)(
    'sends no rotation on %s and restores the opener',
    async (action) => {
      const { root, fixture, slot } = await renderSecretsPage();
      const opener = rotateButton(root);
      opener.focus();
      opener.click();
      await fixture.whenStable();
      if (action === 'cancel')
        document.querySelector<HTMLButtonElement>('[data-rotation-cancel]')?.click();
      else
        document.querySelector('[role="alertdialog"]')?.dispatchEvent(
          new KeyboardEvent('keydown', {
            key: 'Escape',
            keyCode: 27,
            bubbles: true,
            cancelable: true,
          }),
        );
      await fixture.whenStable();
      expect(store.rotate).not.toHaveBeenCalled();
      expect(slot.current()).toBeNull();
      expect(document.querySelector('[role="alertdialog"]')).toBeNull();
      expect(document.activeElement).toBe(opener);
    },
  );

  it.each(['narrow', 'permission', 'inactive'] as const)(
    'rechecks %s before the confirmed request',
    async (change) => {
      const { root, fixture } = await renderSecretsPage();
      rotateButton(root).click();
      await fixture.whenStable();
      const confirm = document.querySelector<HTMLButtonElement>('[data-rotation-confirm-submit]');
      if (!confirm) throw new Error('Missing explicit confirm');
      if (change === 'narrow') wide.set(false);
      if (change === 'permission') writable.set(false);
      if (change === 'inactive') store.rows.set([{ ...base, status: 'rotated' }]);
      confirm.click();
      await fixture.whenStable();
      expect(store.rotate).not.toHaveBeenCalled();
    },
  );

  it('cancels old-project consent when project selection moves', async () => {
    const { root, fixture } = await renderSecretsPage();
    rotateButton(root).click();
    await fixture.whenStable();
    chooseSecretProject(fixture, 'p2');
    await fixture.whenStable();
    expect(document.querySelector('[data-rotation-confirm]')).toBeNull();
    expect(store.rotate).not.toHaveBeenCalled();
    expect(store.load).toHaveBeenLastCalledWith('p2');
  });

  it('clears the reveal and its live input on navigation without exposing a refetch action', async () => {
    const { root, fixture, slot } = await renderSecretsPage();
    rotateButton(root).click();
    await confirmRotation(fixture);
    const input = document.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
    expect(input?.value).toBe('new-full-credential-once');
    navigation.next(new NavigationStart(1, '/safe-destination'));
    await fixture.whenStable();
    expect(slot.current()).toBeNull();
    expect(input?.value).toBe('');
    expect(document.body.outerHTML).not.toContain('new-full-credential-once');
    root.querySelector<HTMLButtonElement>('[data-secret-inspect]')?.click();
    await fixture.whenStable();
    expect(document.querySelector('[data-testid="reveal-value"]')).toBeNull();
    expect(store.rotate).toHaveBeenCalledTimes(1);
  });

  it('drops a one-time response that arrives after navigation started', async () => {
    let complete: ((result: RotationResult) => void) | undefined;
    store.rotate.mockImplementation(
      () =>
        new Promise((resolve) => {
          complete = resolve;
        }),
    );
    const { root, fixture, slot } = await renderSecretsPage();
    rotateButton(root).click();
    await confirmRotation(fixture);
    navigation.next(new NavigationStart(1, '/safe-destination'));
    complete?.({ successorId: 'successor', overlapEndsAt: null, value: 'abandoned-once' });
    await fixture.whenStable();
    expect(slot.current()).toBeNull();
    expect(document.body.outerHTML).not.toContain('abandoned-once');
  });

  it('returns cancellation focus to the page heading when the original row disappeared', async () => {
    const { root, fixture } = await renderSecretsPage();
    rotateButton(root).click();
    await fixture.whenStable();
    store.rows.set([]);
    await fixture.whenStable();
    document.querySelector<HTMLButtonElement>('[data-rotation-cancel]')?.click();
    await fixture.whenStable();
    expect(store.rotate).not.toHaveBeenCalled();
    expect(document.activeElement).toBe(root.querySelector('h2'));
  });

  it('selects scope with Helm project options and retains the complete selected project label', async () => {
    const { root, fixture } = await renderSecretsPage();
    const select = root.querySelector<HTMLButtonElement>('#secrets-project');
    expect(select?.getAttribute('data-slot')).toBe('select-trigger');
    select?.click();
    await fixture.whenStable();
    const option = Array.from(document.querySelectorAll<HTMLElement>('[role="option"]')).find(
      (item) => item.textContent?.trim() === 'API',
    );
    expect(option).toBeDefined();
    option?.click();
    await fixture.whenStable();
    expect(store.load).toHaveBeenLastCalledWith('p2');
    expect(root.querySelector('#secrets-project-value')?.textContent).toBe('API');
  });

  it.each(['dismiss', 'escape-then-dismiss', 'navigation', 'project', 'destroy'])(
    'clears the one-time value on %s, including the DOM input',
    async (action) => {
      const { fixture, root, slot } = await renderSecretsPage();
      const opener = rotateButton(root);
      opener.click();
      await fixture.whenStable();
      await confirmRotation(fixture);
      const input = document.querySelector<HTMLInputElement>('[data-testid="reveal-value"]');
      expect(input?.value).toBe('new-full-credential-once');
      expect(slot.current()?.source).toBe('ingest-token-rotation');
      expect(document.querySelector('[data-testid="reveal-copy"]')).not.toBeNull();
      expect(
        document.querySelector('time[datetime="2026-10-26T00:00:00Z"]')?.textContent,
      ).toContain('UTC');
      if (action === 'escape-then-dismiss') {
        document.querySelector('[role="dialog"]')?.dispatchEvent(
          new KeyboardEvent('keydown', {
            key: 'Escape',
            keyCode: 27,
            bubbles: true,
            cancelable: true,
          }),
        );
        await fixture.whenStable();
        expect(document.querySelector('[data-testid="reveal-value"]')).toBe(input);
        expect(slot.current()?.value).toBe('new-full-credential-once');
        expect(input?.value).toBe('new-full-credential-once');
      }
      if (action === 'dismiss' || action === 'escape-then-dismiss')
        document.querySelector<HTMLButtonElement>('[data-testid="reveal-dismiss"]')?.click();
      if (action === 'navigation') navigation.next(new NavigationStart(2, '/safe-destination'));
      if (action === 'project') chooseSecretProject(fixture, 'p2');
      if (action === 'destroy') fixture.destroy();
      else {
        fixture.detectChanges();
        await fixture.whenStable();
      }
      expect(slot.current()).toBeNull();
      expect(input?.value).toBe('');
      expect(document.body.outerHTML).not.toContain('new-full-credential-once');
      expect(
        JSON.stringify({
          rows: store.rows(),
          phase: store.phase(),
          rotateState: store.rotateState(),
        }),
      ).not.toContain('new-full-credential-once');
      if (action === 'dismiss' || action === 'escape-then-dismiss')
        expect(document.activeElement).toBe(opener);
      if (action === 'project') expect(store.load).toHaveBeenLastCalledWith('p2');
    },
  );
});
