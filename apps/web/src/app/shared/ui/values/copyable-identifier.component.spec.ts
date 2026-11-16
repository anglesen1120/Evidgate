import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { Mock } from 'vitest';

import { CopyableIdentifierComponent } from './copyable-identifier.component';
import { middleTruncate } from './middle-truncate';
import { CopyableIdentifierHarness } from './values.harness';

const value = `sha256:${'0123456789abcdef'.repeat(4)}`;

describe('CopyableIdentifierComponent', () => {
  let fixture: ComponentFixture<CopyableIdentifierComponent>;
  let harness: CopyableIdentifierHarness;
  let clipboard: PropertyDescriptor | undefined;
  let writeText: Mock<(value: string) => Promise<void>>;
  let observer: {
    observe: Mock<ResizeObserver['observe']>;
    unobserve: Mock<ResizeObserver['unobserve']>;
    disconnect: Mock<ResizeObserver['disconnect']>;
  };
  let measure: (() => void) | undefined;

  async function setPreviewWidth(clientWidth: number, scrollWidth: number): Promise<void> {
    fixture.detectChanges();
    await fixture.whenStable();
    const preview = (fixture.nativeElement as HTMLElement).querySelector<HTMLElement>(
      'code > bdi',
    )!;
    vi.spyOn(preview, 'clientWidth', 'get').mockReturnValue(clientWidth);
    vi.spyOn(preview, 'scrollWidth', 'get').mockReturnValue(scrollWidth);
    expect(measure).toBeDefined();
    measure!();
    await fixture.whenStable();
  }

  beforeEach(async () => {
    clipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    writeText = vi.fn<(value: string) => Promise<void>>().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    measure = undefined;
    observer = {
      observe: vi.fn<ResizeObserver['observe']>(),
      unobserve: vi.fn<ResizeObserver['unobserve']>(),
      disconnect: vi.fn<ResizeObserver['disconnect']>(),
    };
    vi.stubGlobal(
      'ResizeObserver',
      vi.fn(function (callback: ResizeObserverCallback) {
        measure = () => callback([], observer);
        return observer;
      }),
    );
    await TestBed.configureTestingModule({
      imports: [CopyableIdentifierComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    // jsdom has no geometry; leave the real CDK overlay/focus lifecycle in place.
    vi.spyOn(TestBed.inject(InteractivityChecker), 'isVisible').mockReturnValue(true);
    fixture = TestBed.createComponent(CopyableIdentifierComponent);
    fixture.componentRef.setInput('value', value);
    fixture.componentRef.setInput('label', 'Policy hash');
    harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, CopyableIdentifierHarness);
  });

  afterEach(() => {
    fixture.destroy();
    TestBed.resetTestingModule();
    if (clipboard) Object.defineProperty(navigator, 'clipboard', clipboard);
    else Reflect.deleteProperty(navigator, 'clipboard');
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it.each(['light', 'dark'])(
    'keeps the full accessible original with a middle preview in %s',
    async (theme) => {
      (fixture.nativeElement as HTMLElement).setAttribute('data-theme', theme);
      expect(await harness.previewText()).toBe(middleTruncate(value));
      expect(await harness.accessibleValue()).toBe(`Policy hash: ${value}`);
      expect(
        (fixture.nativeElement as HTMLElement).querySelector('code')?.classList.contains('preview'),
      ).toBe(true);
    },
  );

  it.each([
    ['bidi override', '\u202E', '⟨U+202E⟩'],
    ['zero-width space', '\u200B', '⟨U+200B⟩'],
    ['zero-width joiner', '\u200D', '⟨U+200D⟩'],
    ['astral format tag', '\u{E0001}', '⟨U+E0001⟩'],
  ])('escapes and isolates %s in every displayed form', async (_, character, token) => {
    const escaped = `feature/${token}release`;
    fixture.componentRef.setInput('value', `feature/${character}release`);
    expect(await harness.previewText()).toBe(escaped);
    expect(await harness.accessibleValue()).toBe(`Policy hash: ${escaped}`);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('code > bdi')?.textContent).toBe(escaped);
    expect(host.querySelector('.identifier-accessible > bdi')?.textContent).toBe(
      `Policy hash: ${escaped}`,
    );
    expect(host.textContent).not.toMatch(/\p{Cf}/u);
    await harness.showFull();
    expect(await harness.fullText()).toBe(escaped);
    expect(document.querySelector('[role="dialog"] code > bdi')?.textContent).toBe(escaped);
    expect(document.querySelector('[role="dialog"]')?.textContent).not.toMatch(/\p{Cf}/u);
    await harness.closeFull();
    fixture.componentRef.setInput('truncate', false);
    expect(await harness.previewText()).toBe(escaped);
    expect(host.querySelector('code.full-value > bdi')?.textContent).toBe(escaped);
  });

  it('escapes before applying the long-value middle preview', async () => {
    const suffix = 'a'.repeat(120);
    fixture.componentRef.setInput('value', `feature/\u202E${suffix}`);
    expect(await harness.previewText()).toBe(middleTruncate(`feature/⟨U+202E⟩${suffix}`));
    expect(await harness.accessibleValue()).toBe(`Policy hash: feature/⟨U+202E⟩${suffix}`);
  });

  it('isolates ordinary RTL text without changing the identifier', async () => {
    const ordinary = 'feature/إصلاح';
    fixture.componentRef.setInput('value', ordinary);
    expect(await harness.previewText()).toBe(ordinary);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('code > bdi')?.textContent).toBe(ordinary);
    await harness.showFull();
    expect(document.querySelector('[role="dialog"] code > bdi')?.textContent).toBe(ordinary);
  });

  it('opens a named top-layer full-value dialog without expanding a fixed-height row', async () => {
    await harness.showFull();
    expect(await harness.fullText()).toBe(value);
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;
    expect(dialog.getAttribute('aria-label')).toBe('Policy hash');
    expect((fixture.nativeElement as HTMLElement).querySelector('[role="dialog"]')).toBeNull();
    expect(await harness.previewText()).toBe(middleTruncate(value));
    await harness.closeFull();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
  });

  it('forwards the requested large copy target to the scan row and full-value dialog', async () => {
    expect(fixture.componentInstance.copySize()).toBe('sm');
    fixture.componentRef.setInput('copySize', 'lg');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const inlineCopy = root.querySelector<HTMLButtonElement>('app-copy-value-button button')!;
    expect(inlineCopy.classList.contains('spartan-button-size-lg')).toBe(true);
    expect(inlineCopy.getAttribute('data-copy-size')).toBe('lg');
    await harness.showFull();
    const dialogCopy = document.querySelector<HTMLButtonElement>(
      '[role="dialog"] app-copy-value-button button',
    )!;
    expect(dialogCopy.classList.contains('spartan-button-size-lg')).toBe(true);
    expect(dialogCopy.getAttribute('data-copy-size')).toBe('lg');
  });

  it('supports Escape and returns focus to the full-value action', async () => {
    const trigger = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      'button',
    )!;
    trigger.focus();
    await harness.showFull();
    const dialog = document.querySelector<HTMLElement>('[role="dialog"]')!;
    expect(dialog.contains(document.activeElement)).toBe(true);
    dialog.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }));
    await fixture.whenStable();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    expect(document.activeElement).toBe(trigger);
  });

  it('retains a full wrapping detail value when truncation is disabled', async () => {
    fixture.componentRef.setInput('truncate', false);
    expect(await harness.previewText()).toBe(value);
    const host = fixture.nativeElement as HTMLElement;
    expect(host.querySelector('code')?.classList.contains('full-value')).toBe(true);
    expect(host.querySelector('button')?.getAttribute('aria-label')).toBe('Copy Policy hash');
  });

  it('provides a full path for short previews too, since a narrow cell can still clip them', async () => {
    fixture.componentRef.setInput('value', 'feature/checkout-banner');
    expect(await harness.previewText()).toBe('feature/checkout-banner');
    await harness.showFull();
    expect(await harness.fullText()).toBe('feature/checkout-banner');
  });

  it.each([false, true])('keeps a short identifier complete with compact=%s', async (compact) => {
    expect(fixture.componentInstance.compact()).toBe(false);
    fixture.componentRef.setInput('value', 'run-123');
    fixture.componentRef.setInput('compact', compact);
    expect(await harness.previewText()).toBe('run-123');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.preview-head')?.textContent).toBe('run-123');
    expect(root.querySelector('.preview-tail')).toBeNull();
    expect(root.querySelectorAll('button')).toHaveLength(compact ? 1 : 2);
    expect(root.querySelector('[aria-haspopup="dialog"]') !== null).toBe(!compact);
    expect(root.querySelector('app-copy-value-button button')?.getAttribute('aria-label')).toBe(
      'Copy Policy hash',
    );
  });

  it('keeps compact rows single-line even when a caller previously used full inline values', async () => {
    fixture.componentRef.setInput('truncate', false);
    fixture.componentRef.setInput('compact', true);
    expect(await harness.previewText()).toBe(`${value.slice(0, 8)}…${value.slice(-7)}`);
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('code')?.classList.contains('preview')).toBe(true);
    expect(root.querySelector('code')?.classList.contains('full-value')).toBe(false);
    await harness.showFull();
    expect(await harness.fullText()).toBe(value);
  });

  it.each(['light', 'dark'])(
    'keeps the head and tail with inline actions in a narrow %s cell',
    async (theme) => {
      const id = '0198a000-0000-7000-8000-00000000d001';
      fixture.componentRef.setInput('value', id);
      fixture.componentRef.setInput('compact', true);
      const root = fixture.nativeElement as HTMLElement;
      root.setAttribute('data-theme', theme);
      root.style.inlineSize = '136px';
      expect(await harness.previewText()).toBe('0198a000…000d001');
      expect(root.querySelector('code > bdi')).not.toBeNull();
      expect(root.querySelector('.preview-head')?.textContent).toBe('0198a000');
      expect(root.querySelector('.preview-tail')?.textContent).toBe('000d001');
      expect(await harness.accessibleValue()).toBe(`Policy hash: ${id}`);
      expect(getComputedStyle(root.querySelector('.identifier')!).flexWrap).toBe('nowrap');
      expect(getComputedStyle(root.querySelector('.actions')!).flexWrap).toBe('nowrap');
      expect(getComputedStyle(root.querySelector('code')!).textOverflow).toBe('clip');
      expect(root.querySelectorAll('button hlm-icon')).toHaveLength(2);
      await harness.showFull();
      expect(await harness.fullText()).toBe(id);
      expect(document.querySelector('[role="dialog"] app-copy-value-button hlm-icon')).toBeNull();
    },
  );

  it.each([
    {
      kind: 'wide glyphs',
      identifier: '漢字識別子測試寬字元',
      display: '漢字識別子測試寬字元',
      unsafe: false,
    },
    { kind: 'escaped format token', identifier: '\u202E', display: '⟨U+202E⟩', unsafe: true },
  ])(
    'marks physical clipping of $kind without a second ellipsis',
    async ({ identifier, display, unsafe }) => {
      fixture.componentRef.setInput('compact', true);
      fixture.componentRef.setInput('value', identifier);
      expect(await harness.previewText()).toBe(display);
      const root = fixture.nativeElement as HTMLElement;
      const preview = root.querySelector('code > bdi')!;
      expect(preview.classList.contains('preview-elided')).toBe(false);
      expect(getComputedStyle(preview).textOverflow).toBe('ellipsis');
      expect(getComputedStyle(root.querySelector('code')!).textOverflow).toBe('clip');
      expect(root.querySelector('.preview-tail')).toBeNull();
      expect(root.querySelector('[aria-haspopup="dialog"]')).toBeNull();
      await setPreviewWidth(128, 192);
      expect(observer.observe).toHaveBeenCalledWith(preview);
      expect(root.querySelector('[aria-haspopup="dialog"]')?.getAttribute('title')).toBe(
        'Show full Policy hash',
      );
      const copy = root.querySelector<HTMLButtonElement>('app-copy-value-button button')!;
      copy.click();
      await fixture.whenStable();
      if (unsafe) expect(writeText).not.toHaveBeenCalled();
      else expect(writeText).toHaveBeenCalledExactlyOnceWith(identifier);
      expect(root.textContent).not.toMatch(/\p{Cf}/u);
      await harness.showFull();
      expect(await harness.fullText()).toBe(display);
      await harness.closeFull();
      expect(document.activeElement).toBe(copy);
      await setPreviewWidth(240, 192);
      expect(root.querySelector('[aria-haspopup="dialog"]')).toBeNull();
      expect(root.querySelector('app-copy-value-button button')).toBe(copy);
    },
  );

  it('observes only non-grid compact previews and disconnects for recycled values', async () => {
    expect(ResizeObserver).not.toHaveBeenCalled();
    fixture.componentRef.setInput('compact', true);
    fixture.componentRef.setInput('value', 'run-123');
    await fixture.whenStable();
    expect(observer.observe).toHaveBeenCalledOnce();
    fixture.componentRef.setInput('value', value);
    await fixture.whenStable();
    expect(observer.disconnect).toHaveBeenCalledOnce();
    expect(ResizeObserver).toHaveBeenCalledOnce();
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[aria-haspopup="dialog"]'),
    ).not.toBeNull();
  });

  it('disconnects a compact preview observer when its owner is destroyed', async () => {
    fixture.componentRef.setInput('compact', true);
    fixture.componentRef.setInput('value', 'run-123');
    await fixture.whenStable();
    expect(observer.observe).toHaveBeenCalledOnce();
    fixture.destroy();
    expect(observer.disconnect).toHaveBeenCalledOnce();
  });

  describe.each([false, true])('middle preview with compact=%s', (compact) => {
    it.each([
      { kind: 'hash', identifier: value, budget: 192.6 },
      { kind: 'hash', identifier: value, budget: 43.5 },
      {
        kind: 'branch',
        identifier: 'refs/heads/feature/checkout-accessibility-improvement-2026-11-15',
        budget: 192.6,
      },
      {
        kind: 'branch',
        identifier: 'refs/heads/feature/checkout-accessibility-improvement-2026-11-15',
        budget: 43.5,
      },
    ])(
      'reserves both ends of a $kind at a $budget px inline budget',
      async ({ identifier, budget }) => {
        fixture.componentRef.setInput('compact', compact);
        fixture.componentRef.setInput('value', identifier);
        const root = fixture.nativeElement as HTMLElement;
        root.style.inlineSize = `${budget}px`;
        expect(await harness.previewText()).toBe(middleTruncate(identifier, compact ? 16 : 32));
        const preview = root.querySelector('code.preview')!;
        const parts = preview.querySelector('bdi')!;
        const head = parts.querySelector('.preview-head')!;
        const tail = parts.querySelector('.preview-tail')!;
        expect(head.textContent).toBe(identifier.slice(0, compact ? 8 : 16));
        expect(tail.textContent).toBe(identifier.slice(compact ? -7 : -15));
        expect(getComputedStyle(preview).textOverflow).toBe('clip');
        expect(getComputedStyle(parts).display).toBe('grid');
        expect(getComputedStyle(parts).gridTemplateColumns).toBe(
          'minmax(0, 1fr) auto minmax(0, 1fr)',
        );
        expect(getComputedStyle(tail).justifyContent).toBe('flex-end');
        expect(getComputedStyle(tail.firstElementChild!).flexShrink).toBe('0');
        expect(await harness.accessibleValue()).toBe(`Policy hash: ${identifier}`);
      },
    );
  });

  it.each([false, true])('copies the complete original in compact=%s', async (compact) => {
    fixture.componentRef.setInput('compact', compact);
    fixture.detectChanges();
    (fixture.nativeElement as HTMLElement)
      .querySelector<HTMLButtonElement>('app-copy-value-button button')!
      .click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledExactlyOnceWith(value);
    expect(
      (fixture.nativeElement as HTMLElement).querySelector('[role="status"]')?.textContent,
    ).toBe('Policy hash copied.');
  });

  it.each(
    [false, true].flatMap((compact) =>
      (
        [
          ['bidi override', '\u202E', '⟨U+202E⟩'],
          ['zero-width space', '\u200B', '⟨U+200B⟩'],
          ['zero-width joiner', '\u200D', '⟨U+200D⟩'],
          ['astral format tag', '\u{E0001}', '⟨U+E0001⟩'],
        ] as const
      ).map(([kind, character, token]) => ({ compact, kind, character, token })),
    ),
  )(
    'refuses $kind and keeps escaped full-value recovery with compact=$compact',
    async ({ compact, character, token }) => {
      fixture.componentRef.setInput('compact', compact);
      fixture.componentRef.setInput('value', `feature/${character}release`);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      root.querySelector<HTMLButtonElement>('app-copy-value-button button')!.click();
      await fixture.whenStable();
      expect(writeText).not.toHaveBeenCalled();
      expect(root.textContent).not.toMatch(/\p{Cf}/u);
      expect(root.querySelector('.identifier-accessible > bdi')?.textContent).toBe(
        `Policy hash: feature/${token}release`,
      );
      expect(root.querySelector('[role="status"]')?.textContent).toContain(
        'Could not copy Policy hash.',
      );
      await harness.showFull();
      expect(await harness.fullText()).toBe(`feature/${token}release`);
    },
  );

  it('closes an open provenance dialog before a recycled row changes identity', async () => {
    await harness.showFull();
    fixture.componentRef.setInput('value', 'next-identifier');
    fixture.detectChanges();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    await fixture.whenStable();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
    expect(await harness.previewText()).toBe('next-identifier');
  });

  it('closes the overlay when its row is destroyed', async () => {
    await harness.showFull();
    fixture.destroy();
    expect(document.querySelector('[role="dialog"]')).toBeNull();
  });
});
