// State sheet: docs/design/web/state-sheet.md#status-pills
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { StatusPillComponent, type StatusTone } from './status-pill.component';

describe('StatusPillComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [StatusPillComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => vi.restoreAllMocks());

  const states: readonly [string, string, StatusTone, string][] = [
    ['default', 'Collecting', 'info', 'Collecting'],
    ['hover', 'Collecting', 'info', 'Collecting'],
    ['focus-visible', 'Collecting', 'info', 'Collecting'],
    ['active', 'Collecting', 'info', 'Collecting'],
    ['disabled', 'Collecting', 'info', 'Collecting'],
    ['loading', 'Collecting', 'info', 'Collecting'],
    ['empty', '', 'success', 'Status unavailable'],
    ['error', 'Collection interrupted', 'danger', 'Collection interrupted'],
    ['success', 'Complete', 'success', 'Complete'],
  ];

  it.each(states)(
    'keeps the %s state render-only and value-driven',
    (_state, label, tone, copy) => {
      const fetch = vi.spyOn(globalThis, 'fetch');
      const fixture = TestBed.createComponent(StatusPillComponent);
      fixture.componentRef.setInput('tone', tone);
      fixture.componentRef.setInput('label', label);
      fixture.componentRef.setInput('dimension', 'Run status');
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;

      expect(root.querySelector('.status-pill__label')?.textContent).toBe(copy);
      expect(root.textContent).toContain('Run status:');
      expect(root.querySelector(`.status-pill--${label ? tone : 'neutral'}`)).not.toBeNull();
      expect(
        root.querySelector('[tabindex], [role], [aria-disabled], [aria-busy], button, a'),
      ).toBeNull();
      expect(root.querySelector('svg, [title]')).toBeNull();
      expect(root.textContent).not.toMatch(/\b(?:PASS|FAIL)\b/);
      expect(fetch).not.toHaveBeenCalled();
      if (!label) {
        expect(root.querySelector('.status-pill--success, .status-pill--danger')).toBeNull();
      }
    },
  );

  it.each(['success', 'info', 'warning', 'danger', 'neutral'] as const)(
    'renders a supplied %s variant without inventing a verdict',
    (tone) => {
      const fixture = TestBed.createComponent(StatusPillComponent);
      fixture.componentRef.setInput('tone', tone);
      fixture.componentRef.setInput('label', 'Localized domain status');
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      expect(root.querySelector(`.status-pill--${tone}`)).not.toBeNull();
      const badge = root.querySelector('[hlmBadge]');
      expect(badge?.getAttribute('data-slot')).toBe('badge');
      expect(badge?.classList.contains(`bg-[var(--eg-${tone}-bg)]`)).toBe(true);
      expect(badge?.classList.contains(`text-[var(--eg-${tone}-fg)]`)).toBe(true);
      expect(root.textContent?.trim()).toBe('Localized domain status');
      expect(root.querySelector('.status-pill__dimension')).toBeNull();
    },
  );

  it.each([null, undefined, '   '])('renders missing label %s as neutral unavailable', (label) => {
    const fixture = TestBed.createComponent(StatusPillComponent);
    fixture.componentRef.setInput('tone', 'success');
    fixture.componentRef.setInput('label', label);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe('Status unavailable');
    expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
    expect(root.querySelector('.status-pill--success')).toBeNull();
  });

  it('wraps long localized labels and preserves untrusted text without markup', () => {
    const fixture = TestBed.createComponent(StatusPillComponent);
    const label = '<img src=x onerror=alert(1)> Đang thu thập bằng chứng bổ sung '.repeat(8);
    fixture.componentRef.setInput('tone', 'info');
    fixture.componentRef.setInput('label', label);
    fixture.componentRef.setInput('dimension', 'Trạng thái lượt chạy');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.status-pill__label')?.textContent).toBe(label);
    expect(root.querySelector('.status-pill__dimension')?.textContent).toBe(
      'Trạng thái lượt chạy:',
    );
    expect(root.querySelector('img')).toBeNull();
    expect(root.querySelector('.status-pill__label.text-truncate, [title]')).toBeNull();
    const badge = root.querySelector('.status-pill');
    expect(badge?.classList.contains('h-auto')).toBe(true);
    expect(badge?.classList.contains('overflow-visible')).toBe(true);
    expect(badge?.classList.contains('rounded-(--eg-radius-sm)')).toBe(true);
    expect(badge?.classList.contains('overflow-hidden')).toBe(false);
  });

  it('updates supplied label and tone in the zoneless tree', async () => {
    const fixture = TestBed.createComponent(StatusPillComponent);
    fixture.componentRef.setInput('tone', 'info');
    fixture.componentRef.setInput('label', 'Collecting');
    await fixture.whenStable();
    fixture.componentRef.setInput('tone', 'warning');
    fixture.componentRef.setInput('label', 'Incomplete');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.textContent?.trim()).toBe('Incomplete');
    expect(root.querySelector('.status-pill--warning')).not.toBeNull();
    expect(root.querySelector('.status-pill--info')).toBeNull();
  });
});
