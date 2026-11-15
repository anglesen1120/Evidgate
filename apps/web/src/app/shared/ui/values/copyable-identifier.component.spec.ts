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

  beforeEach(async () => {
    clipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    writeText = vi.fn<(value: string) => Promise<void>>().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
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

  it('keeps a short compact identifier complete and preserves normal callers', async () => {
    fixture.componentRef.setInput('value', 'run-123');
    expect(fixture.componentInstance.compact()).toBe(false);
    fixture.componentRef.setInput('compact', true);
    expect(await harness.previewText()).toBe('run-123');
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.compact-head')?.textContent).toBe('run-123');
    expect(root.querySelector('.compact-tail')).toBeNull();
    expect(root.querySelectorAll('button')).toHaveLength(2);
    expect(root.querySelector('[aria-haspopup="dialog"]')?.getAttribute('title')).toBe(
      'Show full Policy hash',
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
      expect(root.querySelector('.compact-head')?.textContent).toBe('0198a000');
      expect(root.querySelector('.compact-tail')?.textContent).toBe('000d001');
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

  it('copies the complete compact identifier rather than either preview segment', async () => {
    fixture.componentRef.setInput('compact', true);
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

  it.each([
    ['bidi override', '\u202E', '⟨U+202E⟩'],
    ['zero-width space', '\u200B', '⟨U+200B⟩'],
    ['zero-width joiner', '\u200D', '⟨U+200D⟩'],
    ['astral format tag', '\u{E0001}', '⟨U+E0001⟩'],
  ])(
    'refuses %s and keeps escaped full-value recovery in compact mode',
    async (_, character, token) => {
      fixture.componentRef.setInput('compact', true);
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
