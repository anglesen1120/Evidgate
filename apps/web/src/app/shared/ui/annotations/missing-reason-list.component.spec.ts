// State sheet: docs/design/web/state-sheet.md#missing-reason-list
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { MissingReasonList, type MissingReason } from './missing-reason-list.component';

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
const reasons: readonly MissingReason[] = [
  {
    code: 'missing_shard',
    text: 'Shard #2 is missing <script>untrusted</script>',
    href: '/runs/run-001/attempts/attempt-002',
  },
  { code: 'count_mismatch', text: 'Expected 42 cases; received 41.' },
];

describe('MissingReasonList (FR-09-2/6)', () => {
  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [MissingReasonList],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
  });

  afterEach(() => vi.restoreAllMocks());

  it('composes outlined reason badges and native Helm navigation without making plain reasons controls', () => {
    const fixture = TestBed.createComponent(MissingReasonList);
    fixture.componentRef.setInput('reasons', reasons);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('[data-slot="badge"]')).toHaveLength(2);
    const link = root.querySelector('a');
    expect(link?.getAttribute('data-slot')).toBe('button');
    expect(link?.getAttribute('href')).toBe(reasons[0]?.href);
    expect(link?.classList.contains('whitespace-normal')).toBe(true);
    expect(root.querySelectorAll('a')).toHaveLength(1);
    expect(root.querySelector('.reason-text')?.hasAttribute('tabindex')).toBe(false);
  });

  it.each(states)('covers the %s state without inferring a verdict', (state) => {
    const fixture = TestBed.createComponent(MissingReasonList);
    fixture.componentRef.setInput('reasons', state === 'empty' ? [] : reasons);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;

    if (state === 'empty') {
      expect(root.querySelector('ul')).toBeNull();
      expect(root.textContent?.trim()).toBe('No missing evidence reasons reported');
    } else {
      expect(root.querySelector('ul')?.getAttribute('aria-label')).toBe('Missing evidence reasons');
      const items = root.querySelectorAll('li');
      expect(items).toHaveLength(2);
      expect(items[0]?.querySelector('code')?.textContent).toBe(reasons[0]?.code);
      expect(items[0]?.querySelector('a')?.textContent).toBe(reasons[0]?.text);
      expect(items[1]?.querySelector('.reason-text')?.textContent).toBe(reasons[1]?.text);
      expect(root.querySelector('script')).toBeNull();
      const link = root.querySelector('a');
      expect(link?.getAttribute('href')).toBe(reasons[0]?.href);
      expect(items[1]?.querySelector('a')).toBeNull();
      if (state === 'focus-visible') {
        link?.focus();
        expect(document.activeElement).toBe(link);
        expect(link?.classList.contains('reason-link')).toBe(true);
      } else if (state === 'hover') {
        link?.dispatchEvent(new MouseEvent('mouseover', { bubbles: true }));
      } else if (state === 'active') {
        // Native navigation only; cancel in jsdom rather than navigating away from the fixture.
        link?.addEventListener('click', (event) => event.preventDefault(), { once: true });
        link?.click();
      }
      expect(fixture.componentInstance.reasons()).toBe(reasons);
    }
    // Only supplied anchors are focusable: the list itself never becomes a control.
    expect(root.querySelector('[tabindex], [role="button"], button, [aria-disabled]')).toBeNull();
    expect(
      root.querySelector('[role="status"], [aria-busy="true"], [class*="success"]'),
    ).toBeNull();
    expect(root.textContent).not.toMatch(/\b(PASS|FAIL|complete)\b/);
  });

  it('renders every supplied reason verbatim and only supplied deep links', () => {
    const fixture = TestBed.createComponent(MissingReasonList);
    const allReasons = [
      'missing_shard',
      'count_mismatch',
      'artifact_missing',
      'build_mismatch',
      'unknown_status',
      'stale_epoch',
    ].map((code) => ({
      code,
      text: `Reason ${code}: producer-001 / attempt-042`,
      ...(code === 'artifact_missing'
        ? { href: '/producers/producer-001/attempts/attempt-042' }
        : {}),
    }));
    fixture.componentRef.setInput('reasons', allReasons);
    fixture.componentRef.setInput('heading', 'Lý do thiếu bằng chứng');
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('h3')?.textContent).toBe('Lý do thiếu bằng chứng');
    expect(root.querySelector('ul')?.getAttribute('aria-label')).toBe('Lý do thiếu bằng chứng');
    expect(root.querySelectorAll('a')).toHaveLength(1);
    root.querySelectorAll('li').forEach((item, index) => {
      expect(item.querySelector('code')?.textContent).toBe(allReasons[index]?.code);
      expect(item.querySelector('a, .reason-text')?.textContent).toBe(allReasons[index]?.text);
    });
  });

  it('preserves long Vietnamese text and identifiers in wrapping structures', () => {
    const fixture = TestBed.createComponent(MissingReasonList);
    const text = 'Thiếu bằng chứng từ lần thử gốc; không suy diễn kết quả. '.repeat(30);
    const code = 'stale_epoch_'.repeat(30);
    fixture.componentRef.setInput('reasons', [{ code, text, href: '' }]);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('.reason')?.classList.contains('reason')).toBe(true);
    expect(root.querySelector('.reason-text')?.textContent).toBe(text);
    expect(root.querySelector('code')?.textContent).toBe(code);
    expect(root.querySelector('a, [title]')).toBeNull();
  });

  it.each([
    'data:text/html,<script>alert(1)</script>',
    'javascript:alert(1)',
    '//evil.example/artifact',
    'ftp://evil.example/artifact',
    'mailto:artifact@example.com',
    ' java\tscript:alert(1)',
    '\tjavascript:alert(1)',
    '\njavascript:alert(1)',
    '\u0000javascript:alert(1)',
    'java\tscript:alert(1)',
    '/\\evil.example/artifact',
    '/\t/evil.example/artifact',
    'artifacts/001',
    'https://',
    '',
    '   ',
  ])('renders rejected href %j as plain text without losing the reason', (href) => {
    const fixture = TestBed.createComponent(MissingReasonList);
    const text = 'Unavailable artifact <script>untrusted</script>';
    fixture.componentRef.setInput('reasons', [{ code: 'artifact_missing', text, href }]);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('a')).toBeNull();
    expect(root.querySelector('.reason-text')?.textContent).toBe(text);
    expect(root.querySelector('code')?.textContent).toBe('artifact_missing');
    expect(root.querySelector('script')).toBeNull();
  });

  it.each([
    ['/', null],
    ['/runs/run-001?attempt=2#evidence', null],
    ['#artifact-001', null],
    ['https://evidence.example/artifact?run=001#details', 'noopener noreferrer'],
    ['http://evidence.example/artifact', 'noopener noreferrer'],
    ['HTTPS://evidence.example/artifact', 'noopener noreferrer'],
  ])('renders allowed href %j with the appropriate rel', (href, rel) => {
    const fixture = TestBed.createComponent(MissingReasonList);
    const text = 'Open artifact';
    fixture.componentRef.setInput('reasons', [{ code: 'artifact_missing', text, href }]);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    const anchor = root.querySelector('a');
    expect(anchor?.getAttribute('href')).toBe(href);
    expect(anchor?.getAttribute('rel')).toBe(rel);
    expect(anchor?.textContent).toBe(text);
    expect(root.querySelector('.reason-text')).toBeNull();
  });

  it('updates link safety when the supplied reasons change', async () => {
    const fixture = TestBed.createComponent(MissingReasonList);
    fixture.componentRef.setInput('reasons', [
      {
        code: 'artifact_missing',
        text: 'Open artifact',
        href: 'https://evidence.example/artifact',
      },
    ]);
    await fixture.whenStable();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelector('a')?.getAttribute('rel')).toBe('noopener noreferrer');
    fixture.componentRef.setInput('reasons', [
      { code: 'artifact_missing', text: 'Unavailable artifact', href: '//evil.example/artifact' },
    ]);
    await fixture.whenStable();
    expect(root.querySelector('a')).toBeNull();
    expect(root.querySelector('.reason-text')?.textContent).toBe('Unavailable artifact');
  });
});
