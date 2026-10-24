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
    vi.spyOn(globalThis, 'fetch');
  });

  afterEach(() => vi.restoreAllMocks());

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
        expect(root.querySelector('.label-flaky')?.textContent).toBe('Flaky');
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
          expect(root.querySelector('.label-healed')?.textContent).toBe('Healed with candidate');
          expect(root.querySelector('.candidate-hash')?.textContent).toBe(hash);
        }
      }
      // Hover/focus/active/disabled/loading/error/success introduce no control or fetch lifecycle.
      expect(
        root.querySelector('[tabindex], [role="button"], [aria-disabled], button, a'),
      ).toBeNull();
      expect(
        root.querySelector('[role="status"], [aria-busy="true"], [class*="success"]'),
      ).toBeNull();
      expect(root.textContent).not.toMatch(/\b(PASS|FAIL|complete)\b/);
      expect(globalThis.fetch).not.toHaveBeenCalled();
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
      expect(root.querySelector('.candidate-block .candidate-hash')?.textContent).toBe(hash);
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
        expect(root.querySelector('.label-flaky')?.textContent).toBe('Flaky');
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
    expect(root.querySelector('.candidate-hash')?.textContent).toBe(longHash);
    expect(root.querySelector('.original-failure')?.textContent).toBe(longFailure);
    expect(root.querySelector('.candidate-block')).not.toBeNull();
    expect(root.querySelector('svg, [title]')).toBeNull();
  });
});
