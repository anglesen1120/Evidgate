import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ApiTokenListComponent } from './api-token-list.component';
import { ROW } from './api-tokens.test-support';

describe('ApiTokenListComponent', () => {
  afterEach(() => TestBed.resetTestingModule());

  function render(rows = [ROW]) {
    TestBed.configureTestingModule({
      imports: [ApiTokenListComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(ApiTokenListComponent);
    fixture.componentRef.setInput('rows', rows);
    fixture.detectChanges();
    return fixture.nativeElement as HTMLElement;
  }

  it('renders twenty metadata rows with public prefixes and permission subsets', () => {
    const full = 'credential-that-must-not-be-rendered';
    const root = render(
      Array.from({ length: 20 }, (_, index) => ({
        ...ROW,
        id: `row-${index}`,
        name: `Reader ${index}`,
        token: full,
      })),
    );
    expect(root.querySelectorAll('[data-api-token-row]')).toHaveLength(20);
    expect(root.textContent).toContain('Reader 19');
    expect(root.textContent).toContain(ROW.prefix);
    expect(root.textContent).toContain('run:read');
    expect(root.textContent).not.toContain(full);
    expect(root.querySelectorAll('th[scope="col"]')).toHaveLength(6);
  });

  it('shows never when last use is null and renders a served timestamp otherwise', () => {
    const used = '2026-11-06T12:00:00Z';
    const root = render([ROW, { ...ROW, id: 'used', last_used_at: used }]);
    expect(root.textContent).toContain('Never');
    expect(root.querySelector(`time[datetime="${used}"]`)?.textContent).toBe(used);
  });

  it.each(['active', 'expired', 'revoked'] as const)(
    'renders the server %s state without a browser-clock inference',
    (status) => {
      const root = render([{ ...ROW, status, expires_at: '2000-01-01T00:00:00Z' }]);
      expect(root.querySelector('[data-api-token-row]')?.getAttribute('data-state')).toBe(status);
      expect(root.querySelector('app-status-pill')?.textContent?.trim()).toBe(
        status[0]?.toUpperCase() + status.slice(1),
      );
    },
  );

  it('renders untrusted token names as text, never markup', () => {
    const name = '<img src=x onerror=alert(1)>';
    const root = render([{ ...ROW, name }]);
    expect(root.textContent).toContain(name);
    expect(root.querySelector('img')).toBeNull();
  });

  it('emits active-row revoke intent only when writable and disables the action while busy', () => {
    TestBed.configureTestingModule({
      imports: [ApiTokenListComponent],
      providers: [provideZonelessChangeDetection()],
    });
    const fixture = TestBed.createComponent(ApiTokenListComponent);
    fixture.componentRef.setInput('rows', [ROW, { ...ROW, id: 'old', status: 'revoked' }]);
    fixture.componentRef.setInput('canWrite', true);
    const revoke = vi.fn();
    fixture.componentInstance.revoke.subscribe(revoke);
    fixture.detectChanges();
    const root = fixture.nativeElement as HTMLElement;
    expect(root.querySelectorAll('th[scope="col"]')).toHaveLength(7);
    expect(root.querySelectorAll('[data-api-revoke]')).toHaveLength(1);
    const button = root.querySelector<HTMLButtonElement>('[data-api-revoke]')!;
    button.click();
    expect(revoke).toHaveBeenCalledWith(ROW);
    fixture.componentRef.setInput('busy', true);
    fixture.detectChanges();
    expect(button.disabled).toBe(true);
    button.click();
    expect(revoke).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('canWrite', false);
    fixture.detectChanges();
    expect(root.querySelector('[data-api-revoke]')).toBeNull();
  });
});
