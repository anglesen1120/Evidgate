import { InteractivityChecker } from '@angular/cdk/a11y';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';

import { CopyableIdentifierComponent } from './copyable-identifier.component';
import { middleTruncate } from './middle-truncate';
import { CopyableIdentifierHarness } from './values.harness';

const value = `sha256:${'0123456789abcdef'.repeat(4)}`;

describe('CopyableIdentifierComponent', () => {
  let fixture: ComponentFixture<CopyableIdentifierComponent>;
  let harness: CopyableIdentifierHarness;

  beforeEach(async () => {
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
