import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { provideZonelessChangeDetection } from '@angular/core';
import { type ComponentFixture, TestBed } from '@angular/core/testing';
import type { Mock } from 'vitest';

import { CopyValueButtonComponent } from './copy-value-button.component';
import { CopyValueButtonHarness } from './values.harness';

const original = 'sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';

describe('CopyValueButtonComponent', () => {
  let fixture: ComponentFixture<CopyValueButtonComponent>;
  let harness: CopyValueButtonHarness;
  let clipboard: PropertyDescriptor | undefined;
  let writeText: Mock<(value: string) => Promise<void>>;

  beforeEach(async () => {
    clipboard = Object.getOwnPropertyDescriptor(navigator, 'clipboard');
    writeText = vi.fn<(value: string) => Promise<void>>().mockResolvedValue(undefined);
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText } });
    await TestBed.configureTestingModule({
      imports: [CopyValueButtonComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    fixture = TestBed.createComponent(CopyValueButtonComponent);
    fixture.componentRef.setInput('value', original);
    fixture.componentRef.setInput('label', 'Policy hash');
    harness = await TestbedHarnessEnvironment.harnessForFixture(fixture, CopyValueButtonHarness);
  });

  afterEach(() => {
    fixture.destroy();
    TestBed.resetTestingModule();
    if (clipboard) Object.defineProperty(navigator, 'clipboard', clipboard);
    else Reflect.deleteProperty(navigator, 'clipboard');
    vi.restoreAllMocks();
  });

  it('names the action, copies the unmodified value and announces its context', async () => {
    expect(await harness.actionName()).toBe('Copy Policy hash');
    await harness.copy();
    expect(writeText).toHaveBeenCalledExactlyOnceWith(original);
    expect(await harness.result()).toBe('Policy hash copied.');
    expect(await harness.visibleText()).toBe('Copied');
    const status = (fixture.nativeElement as HTMLElement).querySelector('[role="status"]')!;
    expect(status.getAttribute('aria-live')).toBe('polite');
    expect(status.getAttribute('aria-atomic')).toBe('true');
    expect(status.textContent).not.toContain(original);
  });

  it('accepts an explicit translated action name without changing the copied value', async () => {
    fixture.componentRef.setInput('copyLabel', 'Sao chép mã băm chính sách');
    expect(await harness.actionName()).toBe('Sao chép mã băm chính sách');
    await harness.copy();
    expect(writeText).toHaveBeenCalledExactlyOnceWith(original);
  });

  it.each(['denied', 'unavailable'])(
    'announces manual recovery on %s clipboard access',
    async (mode) => {
      if (mode === 'denied') writeText.mockRejectedValue(new Error('Permission denied'));
      else Object.defineProperty(navigator, 'clipboard', { configurable: true, value: undefined });
      await harness.copy();
      expect(await harness.visibleText()).toBe('Copy failed');
      expect(await harness.result()).toContain('Could not copy Policy hash.');
      expect(await harness.result()).toContain('Show the full value and copy it manually.');
    },
  );

  it('allows retry after a refusal', async () => {
    writeText.mockRejectedValueOnce(new Error('Denied'));
    await harness.copy();
    await harness.copy();
    expect(writeText).toHaveBeenCalledTimes(2);
    expect(await harness.result()).toBe('Policy hash copied.');
  });

  it('suppresses duplicate pending actions and ignores a completion for a recycled value', async () => {
    let finish!: () => void;
    writeText.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    );
    const button = (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>(
      'button',
    )!;
    button.click();
    button.click();
    expect(writeText).toHaveBeenCalledExactlyOnceWith(original);
    fixture.componentRef.setInput('value', 'new-request-id');
    fixture.detectChanges();
    finish();
    await fixture.whenStable();
    expect(await harness.result()).toBe('');
    expect(await harness.visibleText()).toBe('Copy');
  });

  it('does not copy an absent identifier or invent a success', async () => {
    fixture.componentRef.setInput('value', '');
    await harness.copy();
    expect(writeText).not.toHaveBeenCalled();
    expect(await harness.result()).toBe('');
  });

  it('ignores clipboard completion after destruction', async () => {
    let finish!: () => void;
    writeText.mockImplementation(
      () =>
        new Promise<void>((resolve) => {
          finish = resolve;
        }),
    );
    (fixture.nativeElement as HTMLElement).querySelector<HTMLButtonElement>('button')!.click();
    fixture.destroy();
    finish();
    await Promise.resolve();
    expect(writeText).toHaveBeenCalledExactlyOnceWith(original);
  });
});
