import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { By } from '@angular/platform-browser';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';

import { BADGE_ICON_NAMES } from './badge-classes';
import { StatusPillComponent, type StatusTone } from './status-pill.component';

describe('StatusPillComponent', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [StatusPillComponent],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => vi.restoreAllMocks());

  it.each(['success', 'info', 'warning', 'danger', 'neutral'] as const)(
    'renders a decorative shared icon for %s without inferring a release decision',
    (tone: StatusTone) => {
      const fetch = vi.spyOn(globalThis, 'fetch');
      const fixture = TestBed.createComponent(StatusPillComponent);
      fixture.componentRef.setInput('tone', tone);
      fixture.componentRef.setInput('label', 'Collecting');
      fixture.componentRef.setInput('dimension', 'Run status');
      fixture.detectChanges();
      const root = fixture.nativeElement as HTMLElement;
      const badge = root.querySelector('[hlmBadge]');
      const icon = badge?.querySelector('hlm-icon');
      expect(root.querySelector(`.status-pill--${tone}`)).not.toBeNull();
      expect(icon?.getAttribute('aria-hidden')).toBe('true');
      expect(icon?.querySelector('svg')).not.toBeNull();
      const renderedIcon = fixture.debugElement.query(By.directive(HlmIcon))
        .componentInstance as HlmIcon;
      expect(renderedIcon.name()).toBe(BADGE_ICON_NAMES[tone]);
      expect(badge?.textContent?.trim()).toBe('Collecting');
      expect(root.querySelector('.status-pill__dimension')?.textContent?.trim()).toBe(
        'Run status:',
      );
      expect(badge?.querySelector('.status-pill__dimension')).toBeNull();
      expect(root.querySelector('[tabindex], [role], button, a, [title]')).toBeNull();
      expect(root.textContent).not.toMatch(/PASS|FAIL/);
      expect(fetch).not.toHaveBeenCalled();
    },
  );

  it.each([null, undefined, '', '   '])('keeps missing label %s neutral and short', (label) => {
    const fixture = TestBed.createComponent(StatusPillComponent);
    fixture.componentRef.setInput('tone', 'success');
    fixture.componentRef.setInput('label', label);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.status-pill__label')?.textContent).toBe('Status unavailable');
    expect(root.querySelector('.status-pill--neutral')).not.toBeNull();
    expect(root.querySelector('.status-pill--success')).toBeNull();
  });

  it('keeps a 60-grapheme Vietnamese source on one line and reasons outside the badge', async () => {
    const fixture = TestBed.createComponent(StatusPillComponent);
    const label = 'Chưa đánh giá đầy đủ bằng chứng phiên bản thử nghiệm quốc tế';
    const detail = '<img src=x onerror=alert(1)> Lý do đầy đủ không được rút gọn.';
    fixture.componentRef.setInput('tone', 'info');
    fixture.componentRef.setInput('label', label);
    fixture.componentRef.setInput('detail', detail);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const badge = root.querySelector('.status-pill');
    const value = root.querySelector('.status-pill__label');
    expect([...new Intl.Segmenter('vi', { granularity: 'grapheme' }).segment(label)]).toHaveLength(
      60,
    );
    expect(value?.textContent).toBe(label);
    expect(value?.classList.contains('whitespace-nowrap')).toBe(true);
    expect(badge?.classList.contains('flex-nowrap')).toBe(true);
    expect(badge?.classList.contains('min-h-[var(--eg-space-7)]')).toBe(true);
    expect(badge?.classList.contains('rounded-(--eg-radius-sm)')).toBe(true);
    expect(badge?.classList.contains('h-auto')).toBe(true);
    expect(badge?.classList.contains('h-5')).toBe(false);
    expect(root.querySelector('.status-pill__detail')?.textContent?.trim()).toBe(detail);
    expect(badge?.querySelector('.status-pill__detail')).toBeNull();
    expect(root.querySelector('img, [title]')).toBeNull();
  });

  it.each(['light', 'dark'])('uses matching semantic pairs in the %s theme', async (theme) => {
    const fixture = TestBed.createComponent(StatusPillComponent);
    fixture.componentRef.setInput('tone', 'warning');
    fixture.componentRef.setInput('label', 'Incomplete');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    root.dataset['theme'] = theme;
    const badge = root.querySelector('[hlmBadge]');
    expect(badge?.classList.contains('bg-[var(--eg-warning-bg)]')).toBe(true);
    expect(badge?.classList.contains('text-[var(--eg-warning-fg)]')).toBe(true);
    // Shared primitive CSS owns line height; merged duplicate utilities are not its contract.
    expect(badge?.classList.contains('spartan-badge')).toBe(true);
  });

  it('keeps complete-value disclosure optional and forwards subsequent changes', async () => {
    const fixture = TestBed.createComponent(StatusPillComponent);
    fixture.componentRef.setInput('tone', 'neutral');
    fixture.componentRef.setInput('label', 'Unknown');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    const badgeElement = fixture.debugElement.query(By.directive(HlmBadge));
    if (!badgeElement) {
      throw new Error('Expected the status badge');
    }
    const badge = badgeElement.injector.get(HlmBadge);
    expect(badge.fullValue()).toBeNull();
    expect(root.querySelector('.spartan-badge__full-value')).toBeNull();

    const complete = 'Unknown run status: future_state';
    fixture.componentRef.setInput('fullValue', complete);
    fixture.componentRef.setInput('detail', complete);
    await fixture.whenStable();
    expect(badge.fullValue()).toBe(complete);
    expect(root.querySelector('.status-pill__label')?.textContent).toBe('Unknown');
    expect(root.querySelector('[hlmBadge]')?.getAttribute('tabindex')).toBe('0');
    expect(root.querySelector('.spartan-badge__label')?.getAttribute('aria-hidden')).toBe('true');
    expect(root.querySelector('[hlmBadge] .sr-only')?.textContent).toBe(complete);
    expect(root.querySelector('.status-pill__detail bdi')?.textContent).toBe(complete);

    fixture.componentRef.setInput('fullValue', null);
    await fixture.whenStable();
    expect(badge.fullValue()).toBeNull();
    expect(root.querySelector('.spartan-badge__full-value')).toBeNull();
  });

  it('replaces the previous supplied tone without retaining success', async () => {
    const fixture = TestBed.createComponent(StatusPillComponent);
    fixture.componentRef.setInput('tone', 'success');
    fixture.componentRef.setInput('label', 'Complete');
    await fixture.whenStable();
    fixture.componentRef.setInput('tone', 'warning');
    fixture.componentRef.setInput('label', 'Incomplete');
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.status-pill__label')?.textContent).toBe('Incomplete');
    expect(root.querySelector('.status-pill--warning')).not.toBeNull();
    expect(root.querySelector('.status-pill--success')).toBeNull();
  });
});
