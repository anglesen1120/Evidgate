// State sheet: docs/design/web/state-sheet.md#revision-chip
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { RevisionChipComponent } from './revision-chip.component';

describe('RevisionChipComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RevisionChipComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => vi.restoreAllMocks());

  it.each([
    'default',
    'hover',
    'focus-visible',
    'active',
    'disabled',
    'loading',
    'empty',
    'error',
    'success',
  ])('keeps the %s state render-only without choosing another revision', (state) => {
    const fetch = vi.spyOn(globalThis, 'fetch');
    const fixture = TestBed.createComponent(RevisionChipComponent);
    fixture.componentRef.setInput('revision', state === 'empty' ? '' : 'ev-0003');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(
      root.querySelector('[tabindex], [role], [aria-disabled], [aria-busy], button, a'),
    ).toBeNull();
    expect(root.querySelector('[title], svg')).toBeNull();
    expect(root.querySelector('[class*="success"], [class*="danger"]')).toBeNull();
    expect(root.textContent).not.toMatch(/\b(?:PASS|FAIL|Complete)\b/);
    expect(fetch).not.toHaveBeenCalled();
    if (state === 'empty') {
      expect(root.textContent?.trim()).toBe('Revision unavailable');
      expect(root.querySelector('.identity-chip__value')).toBeNull();
    } else {
      expect(root.querySelector('.identity-chip__value')?.textContent).toBe('rev-0003');
      expect(root.querySelector('.identity-chip__label')?.textContent).toBe('Revision');
    }
  });

  it.each([null, undefined, '', '   ', '\t\n', 0, -1, 1.5, NaN, Infinity, -Infinity])(
    'does not invent a revision for missing or invalid %s',
    (revision) => {
      const fixture = TestBed.createComponent(RevisionChipComponent);
      fixture.componentRef.setInput('revision', revision);
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.textContent?.trim()).toBe('Revision unavailable');
      expect(root.querySelector('.identity-chip__value')).toBeNull();
    },
  );

  it.each([
    [1, 'r1'],
    [Number.MAX_SAFE_INTEGER, `r${Number.MAX_SAFE_INTEGER}`],
    [5, 'r5'],
    ['0005', 'r0005'],
    ['policy-0007', 'rpolicy-0007'],
    ['bản-0001', 'rbản-0001'],
  ])('preserves locale-independent supplied identity %s', (revision, identity) => {
    const fixture = TestBed.createComponent(RevisionChipComponent);
    fixture.componentRef.setInput('revision', revision);
    fixture.componentRef.setInput('label', 'Phiên bản bằng chứng');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.identity-chip__value')?.textContent).toBe(identity);
    expect(root.querySelector('.identity-chip__label')?.textContent).toBe('Phiên bản bằng chứng');
  });

  it('permits surrounding field context instead of a duplicate label', () => {
    const fixture = TestBed.createComponent(RevisionChipComponent);
    fixture.componentRef.setInput('revision', '0003');
    fixture.componentRef.setInput('label', '');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe('r0003');
    expect(root.querySelector('.identity-chip__label')).toBeNull();
  });

  it('keeps long untrusted ids and localized context in the wrapping structure', () => {
    const fixture = TestBed.createComponent(RevisionChipComponent);
    const revision = '<img src=x onerror=alert(1)>'.repeat(20);
    const label = 'Phiên bản chính sách đối chiếu đã được lưu giữ '.repeat(8);
    fixture.componentRef.setInput('revision', revision);
    fixture.componentRef.setInput('label', label);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.revision-chip .identity-chip__value')?.textContent).toBe(
      `r${revision}`,
    );
    expect(root.querySelector('.identity-chip__label')?.textContent).toBe(label);
    expect(root.querySelector('img, [title]')).toBeNull();
  });

  it('updates only when the owner supplies a different revision', async () => {
    const fixture = TestBed.createComponent(RevisionChipComponent);
    fixture.componentRef.setInput('revision', 3);
    await fixture.whenStable();
    fixture.componentRef.setInput('revision', 4);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.identity-chip__value')?.textContent).toBe('r4');
    expect(root.textContent).not.toContain('r3');
  });
});
