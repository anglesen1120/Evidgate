// State sheet: docs/design/web/state-sheet.md#flaky-label-chip
// State sheet: docs/design/web/state-sheet.md#healed-with-candidate-hash-label-chip
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { LabelChip, type LabelKind } from './label-chip.component';

const states = [
  'default',
  'hover',
  'focus-visible',
  'active',
  'disabled',
  'loading',
  'empty',
  'error',
  'success',
] as const;
const hash = 'sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';
const originalFailure =
  'attempt #1: has_failures\nAssertionError <script>untrusted</script>\nevidence_revision=rev-0042';

describe('LabelChip (FR-09-2/3/4)', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [LabelChip],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('keeps a short status chip and a complete copyable candidate outside it', async () => {
    const fixture = TestBed.createComponent(LabelChip);
    const value = hash.repeat(3);
    fixture.componentRef.setInput('kind', 'healed');
    fixture.componentRef.setInput('candidateHash', value);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const badge = root.querySelector('.label-healed');
    expect(badge?.getAttribute('data-slot')).toBe('badge');
    expect(badge?.textContent?.trim()).toBe('Healed');
    const candidate = root.querySelector('.candidate-hash code');
    expect(candidate?.textContent).toBe(value);
    expect(candidate?.classList.contains('full-value')).toBe(true);
    expect(candidate?.closest('[hlmBadge]')).toBeNull();
    expect(root.querySelector('button')?.getAttribute('aria-label')).toBe('Copy Candidate hash');
    expect(
      root.querySelector('[title], [aria-haspopup], [role="button"], [role="alert"]'),
    ).toBeNull();
  });

  it.each([
    { kind: 'flaky', candidateHash: '', text: 'Flaky' },
    { kind: 'healed', candidateHash: hash, text: 'Healed' },
    { kind: 'healed', candidateHash: '', text: 'Unavailable' },
    { kind: 'unknown', candidateHash: '', text: 'Label unavailable' },
  ])(
    'keeps the "$text" chip single-line with complete metadata outside',
    async ({ kind, candidateHash, text }) => {
      const fixture = TestBed.createComponent(LabelChip);
      fixture.componentRef.setInput('kind', kind);
      fixture.componentRef.setInput('candidateHash', candidateHash);
      await fixture.whenStable();
      const root = fixture.nativeElement as HTMLElement;
      const badge = root.querySelector('.label');
      expect(badge?.textContent?.trim()).toBe(text);
      expect(badge?.classList.contains('h-auto')).toBe(true);
      expect(badge?.classList.contains('h-5')).toBe(false);
      expect(badge?.classList.contains('whitespace-nowrap')).toBe(true);
      expect(badge?.classList.contains('min-h-[var(--eg-space-7)]')).toBe(true);
      expect(badge?.classList.contains('max-w-[min(100%,24ch)]')).toBe(true);
      expect(badge?.classList.contains('rounded-(--eg-radius-sm)')).toBe(true);
      expect(badge?.querySelector('hlm-icon')?.getAttribute('aria-hidden')).toBe('true');
      const graphemes = new Intl.Segmenter('en', { granularity: 'grapheme' }).segment(text);
      expect([...graphemes].length).toBeLessThanOrEqual(20);
      if (kind === 'healed') {
        const detail = root.querySelector('.candidate-description, .candidate-unavailable');
        expect(detail?.textContent?.trim()).toBe(
          candidateHash ? 'Healed with candidate' : 'Candidate hash unavailable',
        );
        expect(detail?.closest('[hlmBadge]')).toBeNull();
      }
    },
  );

  describe.each<LabelKind>(['flaky', 'healed'])('%s label', (kind) => {
    it.each(states)('covers the %s state without changing the original outcome', (state) => {
      const fixture = TestBed.createComponent(LabelChip);
      fixture.componentRef.setInput('kind', kind);
      fixture.componentRef.setInput('originalFailure', originalFailure);
      fixture.componentRef.setInput('candidateHash', state === 'empty' ? '' : hash);
      fixture.componentRef.setInput('mode', 'shadow');
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.original-failure')?.textContent).toBe(originalFailure);
      expect(root.querySelector('script')).toBeNull();
      if (kind === 'flaky') {
        expect(root.querySelector('.label-flaky')?.textContent?.trim()).toBe('Flaky');
        expect(root.querySelector('.label-explanation')?.textContent).toContain(
          'It never overrides attempt #1 or the stored has_failures outcome.',
        );
        expect(root.querySelector('.candidate-hash, .label-healed')).toBeNull();
      } else {
        expect(root.querySelector('.original-block')).not.toBeNull();
        expect(root.querySelector('.candidate-block')).not.toBeNull();
        expect(root.querySelector('.label-mode')?.textContent).toBe('Shadow evaluation');
        if (state === 'empty') {
          expect(root.textContent).toContain('Candidate hash unavailable');
          expect(root.querySelector('.candidate-hash, .label-healed')).toBeNull();
          expect(root.textContent).not.toContain('Healed');
        } else {
          expect(root.querySelector('.label-healed')?.textContent?.trim()).toBe('Healed');
          expect(root.querySelector('.candidate-description')?.textContent?.trim()).toBe(
            'Healed with candidate',
          );
          expect(root.querySelector('.candidate-hash code')?.textContent).toBe(hash);
        }
      }
      // Only the supplied candidate's copy action is interactive; labels do not change outcomes.
      const buttons = root.querySelectorAll('button');
      expect(buttons).toHaveLength(kind === 'healed' && state !== 'empty' ? 1 : 0);
      buttons.forEach((button) =>
        expect(button.getAttribute('aria-label')).toBe('Copy Candidate hash'),
      );
      expect(root.querySelector('a, [tabindex], [role="button"], [aria-busy="true"]')).toBeNull();
      root
        .querySelectorAll('[role="status"]')
        .forEach((status) => expect(status.textContent).toBe(''));
      expect(root.querySelector('[class*="success"]')).toBeNull();
      expect(root.textContent).not.toMatch(/\b(PASS|FAIL|complete)\b/);
    });
  });

  it.each(['shadow', 'official'] as const)(
    'binds the supplied hash to its %s group while preserving the failure block',
    (mode) => {
      const fixture = TestBed.createComponent(LabelChip);
      fixture.componentRef.setInput('kind', 'healed');
      fixture.componentRef.setInput('candidateHash', hash);
      fixture.componentRef.setInput('mode', mode);
      fixture.componentRef.setInput('originalFailure', originalFailure);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.candidate-block .candidate-hash code')?.textContent).toBe(hash);
      expect(root.querySelector('.candidate-block .label-mode')?.textContent).toBe(
        mode === 'shadow' ? 'Shadow evaluation' : 'Official evaluation',
      );
      const failureBlock = root.querySelector('.original-block');
      expect(failureBlock?.querySelector('.original-failure')?.textContent).toBe(originalFailure);
      expect(failureBlock?.nextElementSibling).toBe(root.querySelector('.candidate-block'));
    },
  );

  describe.each<LabelKind>(['flaky', 'healed'])('%s unavailable original failure', (kind) => {
    it.each(['', '   ', '\t\n\r'])('does not render an empty failure block for %j', (failure) => {
      const fixture = TestBed.createComponent(LabelChip);
      fixture.componentRef.setInput('kind', kind);
      fixture.componentRef.setInput('originalFailure', failure);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.original-failure')).toBeNull();
      if (kind === 'healed') {
        expect(root.querySelector('.original-block')?.textContent).toContain(
          'Original failure unavailable',
        );
      } else {
        expect(root.querySelector('.label-flaky')?.textContent?.trim()).toBe('Flaky');
      }
    });

    it('preserves surrounding whitespace in a supplied failure', () => {
      const fixture = TestBed.createComponent(LabelChip);
      const failure = ` \n${originalFailure}\t `;
      fixture.componentRef.setInput('kind', kind);
      fixture.componentRef.setInput('originalFailure', failure);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector('.original-failure')?.textContent).toBe(failure);
    });
  });

  it.each(['', '   '])(
    'does not invent healed metadata for an unavailable hash (%j)',
    (candidateHash) => {
      const fixture = TestBed.createComponent(LabelChip);
      fixture.componentRef.setInput('kind', 'healed');
      fixture.componentRef.setInput('candidateHash', candidateHash);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.textContent).toContain('Candidate hash unavailable');
      expect(root.textContent).toContain('Evaluation group unavailable');
      expect(root.textContent).toContain('Original failure unavailable');
      expect(root.querySelector('.label-healed, .candidate-hash')).toBeNull();
      expect(root.textContent).not.toContain('Healed');
    },
  );

  it('does not invent a recognized label for unknown presentation data', () => {
    const fixture = TestBed.createComponent(LabelChip);
    fixture.componentRef.setInput('kind', 'unknown');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe('Label unavailable');
    expect(root.querySelector('.label-flaky, .label-healed, .candidate-hash')).toBeNull();
  });

  it('preserves untrusted long hashes and failures as wrapping text, with no tooltip-only data', () => {
    const fixture = TestBed.createComponent(LabelChip);
    const longHash = '<svg onload=alert(1)>'.repeat(30);
    const longFailure = 'Lỗi gốc vẫn được hiển thị, không thay thế bằng kết quả chữa lành. '.repeat(
      40,
    );
    fixture.componentRef.setInput('kind', 'healed');
    fixture.componentRef.setInput('candidateHash', longHash);
    fixture.componentRef.setInput('mode', 'official');
    fixture.componentRef.setInput('originalFailure', longFailure);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.candidate-hash code')?.textContent).toBe(longHash);
    expect(root.querySelector('.original-failure')?.textContent).toBe(longFailure);
    expect(root.querySelector('.candidate-block')).not.toBeNull();
    expect(root.querySelector('svg[onload], script, [onerror], [title]')).toBeNull();
  });

  it('copies the complete candidate without rewriting the original failure', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const fixture = TestBed.createComponent(LabelChip);
    const value = ` \n${hash.repeat(3)}\t `;
    fixture.componentRef.setInput('kind', 'healed');
    fixture.componentRef.setInput('candidateHash', value);
    fixture.componentRef.setInput('originalFailure', originalFailure);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const copy = root.querySelector<HTMLButtonElement>('button[aria-label="Copy Candidate hash"]');
    if (!copy) throw new Error('Expected the named candidate-hash copy action');
    copy.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenCalledExactlyOnceWith(value);
    expect(root.querySelector('.candidate-hash code')?.textContent).toBe(value);
    expect(root.querySelector('.original-failure')?.textContent).toBe(originalFailure);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('copied');
  });
});
