import { registerLocaleData } from '@angular/common';
import viLocale from '@angular/common/locales/vi';
import { LOCALE_ID, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { AttemptsTableComponent } from './attempts-table.component';
import { render, row } from './attempts-table.test-support';

describe('Attempts table shared presentation', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [AttemptsTableComponent],
      providers: [provideZonelessChangeDetection()],
    });
  });
  afterEach(() => {
    TestBed.resetTestingModule();
    vi.unstubAllGlobals();
  });

  it('budgets execution identity separately from numeric utility columns', async () => {
    const { root } = await render([row()]);
    expect(root.querySelector('table')?.classList.contains('eg-table-layout')).toBe(true);
    expect(root.querySelector('table')?.classList.contains('table-fixed')).toBe(false);
    const headers = [...root.querySelectorAll('thead th')];
    expect(headers.map((header) => header.getAttribute('data-column-role'))).toEqual([
      'identity',
      'text',
      'status',
      'number',
      'number',
    ]);
    expect(headers[0]?.getAttribute('data-column-priority')).toBe('primary');
    expect(headers[0]?.getAttribute('data-column-kind')).toBe('identifier');
    expect(root.querySelectorAll('td[data-column-role="number"]')).toHaveLength(2);
    expect(root.querySelector('h2')?.getAttribute('data-title-size')).toBe('section');
  });

  it('localizes fractional durations and grouped artifact counts in Vietnamese', async () => {
    registerLocaleData(viLocale);
    TestBed.overrideProvider(LOCALE_ID, { useValue: 'vi' });
    const { root } = await render([row({ durationMs: 1250, artifactCount: 1234 })]);
    expect(
      root.querySelector('[data-attempt-duration] [data-attempt-value]')?.textContent?.trim(),
    ).toBe('1,25 s');
    expect(
      root.querySelector('[data-attempt-artifacts] [data-attempt-value]')?.textContent?.trim(),
    ).toBe('1.234');
  });

  it('copies original execution and candidate identifiers while escaping their visible form', async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal('navigator', { clipboard: { writeText } });
    const executionKey = 'exec\u202e<script>';
    const candidateHash = 'sha256:hash\u202e<script>';
    const { root, fixture } = await render([
      row({
        executionKey,
        healingCandidate: { candidateHash, mode: 'shadow' },
      }),
    ]);
    const identity = root.querySelector('[data-attempt-identity]');
    expect(identity?.classList.contains('grid')).toBe(true);
    expect(identity?.classList.contains('justify-items-start')).toBe(true);
    expect(identity?.classList.contains('gap-(--eg-space-2)')).toBe(true);
    expect(identity?.querySelector('bdi')?.textContent).toBe('exec⟨U+202E⟩<script>');
    const value = identity?.querySelector('bdi');
    const copy = identity?.querySelector('app-copy-value-button');
    expect(copy?.parentElement).toBe(value?.parentElement);
    expect(value?.parentElement?.classList.contains('flex')).toBe(true);
    expect(value?.parentElement?.classList.contains('min-w-0')).toBe(true);
    expect(copy?.classList.contains('shrink-0')).toBe(true);
    identity?.querySelector<HTMLButtonElement>('app-copy-value-button button')?.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenLastCalledWith(executionKey);
    root
      .querySelector<HTMLButtonElement>('[data-healing-candidate] app-copy-value-button button')
      ?.click();
    await fixture.whenStable();
    expect(writeText).toHaveBeenLastCalledWith(candidateHash);
    expect(root.querySelector('script')).toBeNull();
    expect(root.textContent).not.toContain('\u202e');
  });

  it('keeps the full safe identifier available when clipboard permission is denied', async () => {
    vi.stubGlobal('navigator', {
      clipboard: {
        writeText: vi.fn().mockRejectedValue(new Error('denied')),
      },
    });
    const executionKey = 'execution-'.repeat(80);
    const { root, fixture } = await render([row({ executionKey })]);
    root
      .querySelector<HTMLButtonElement>('[data-attempt-identity] app-copy-value-button button')
      ?.click();
    await fixture.whenStable();
    expect(root.querySelector('[data-attempt-identity] bdi')?.textContent).toBe(executionKey);
    expect(root.querySelector('app-copy-value-button [role="status"]')?.textContent).toContain(
      'Could not copy Execution.',
    );
  });
});
