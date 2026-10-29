import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ApiError } from '../../../../core/http/api-error';
import { groupAttempts, paginateAttemptGroups } from './attempt-groups';
import type { AttemptRow } from './attempt-summary';
import { AttemptsTableComponent } from './attempts-table.component';

type AttemptStatus = 'passed' | 'failed' | 'skipped' | 'timed_out' | 'error' | 'blocked';

/** Presentation rows (the wire mapping is pinned by run-detail-store.spec.ts). */
function row(overrides: Partial<AttemptRow> = {}): AttemptRow {
  return {
    executionKey: 'checkout',
    attemptNo: 1,
    rawStatus: 'provider-failure',
    normalisedStatus: 'failed',
    durationMs: 320,
    artifactCount: 2,
    ...overrides,
  };
}
const statuses: AttemptStatus[] = ['passed', 'failed', 'skipped', 'timed_out', 'error', 'blocked'];

describe('retry-chain grouping', () => {
  it('preserves first-seen execution keys, sorts retries and never mutates caller input', () => {
    const input = [
      row({ executionKey: 'b', attemptNo: 3 }),
      row({ executionKey: 'a', attemptNo: 2 }),
      row({ executionKey: 'b', attemptNo: 1 }),
      row({ executionKey: 'a', attemptNo: 1 }),
    ];
    const before = [...input];
    const grouped = groupAttempts(input);
    expect(grouped.map((group) => group.executionKey)).toEqual(['b', 'a']);
    expect(grouped.map((group) => group.attempts.map((attempt) => attempt.attemptNo))).toEqual([
      [1, 3],
      [1, 2],
    ]);
    expect(input).toEqual(before);
  });
  it('pins only the earliest failing attempt when a healing candidate exists', () => {
    const input = [
      row({ attemptNo: 1, normalisedStatus: 'skipped' }),
      row({ attemptNo: 2 }),
      row({ attemptNo: 3, normalisedStatus: 'error' }),
      row({
        attemptNo: 4,
        normalisedStatus: 'passed',
        healingCandidate: { candidateHash: 'hash', mode: 'shadow' },
      }),
    ];
    const [group] = groupAttempts(input);
    expect(group!.preHealingFailure).toBe(input[1]);
    expect(group!.attempts.map((attempt) => attempt.attemptNo)).toEqual([2, 1, 3, 4]);
    expect(groupAttempts(input.slice(0, 3))[0]!.preHealingFailure).toBeNull();
  });
  it('does not invent a failure if a candidate has no failed attempt', () => {
    const [group] = groupAttempts([
      row({
        normalisedStatus: 'passed',
        healingCandidate: { candidateHash: 'hash', mode: 'official' },
      }),
    ]);
    expect(group!.preHealingFailure).toBeNull();
  });
  it('retains every attempt across bounded pages, including a 5000-attempt retry chain', () => {
    const input = Array.from({ length: 5000 }, (_, index) => row({ attemptNo: index + 1 }));
    const pages = paginateAttemptGroups(groupAttempts(input));
    expect(pages).toHaveLength(100);
    expect(
      pages.every((page) => page.reduce((count, group) => count + group.attempts.length, 0) <= 50),
    ).toBe(true);
    expect(pages.flatMap((page) => page.flatMap((group) => group.attempts))).toEqual(input);
  });
});

describe('AttemptsTableComponent', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [AttemptsTableComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());

  async function render(attempts: AttemptRow[] | null) {
    const fixture = TestBed.createComponent(AttemptsTableComponent);
    fixture.componentRef.setInput('attempts', attempts);
    await fixture.whenStable();
    return { fixture, root: fixture.nativeElement as HTMLElement };
  }
  function rows(root: HTMLElement) {
    return [...root.querySelectorAll<HTMLElement>('[data-attempt-row]')];
  }

  it.each(statuses)(
    'renders %s verbatim next to raw status and only passed has success tone',
    async (status) => {
      const { root } = await render([
        row({ rawStatus: 'provider:' + status, normalisedStatus: status }),
      ]);
      const attempt = rows(root)[0]!;
      expect(attempt.querySelector('[data-attempt-raw] bdi')?.textContent).toBe(
        'provider:' + status,
      );
      expect(attempt.querySelector('[data-attempt-normalised] bdi')?.textContent?.trim()).toBe(
        status,
      );
      expect(attempt.querySelectorAll('.status-pill--success')).toHaveLength(
        status === 'passed' ? 1 : 0,
      );
      const cells = [...attempt.children];
      expect(cells[1]?.hasAttribute('data-attempt-raw')).toBe(true);
      expect(cells[2]?.hasAttribute('data-attempt-normalised')).toBe(true);
      if (status === 'blocked') expect(attempt.textContent).not.toContain('passed');
    },
  );

  it('shows both raw and normalised statuses for every attempt, not a merged chain result', async () => {
    const { root } = await render(
      statuses.map((status, index) =>
        row({
          attemptNo: index + 1,
          rawStatus: 'raw-' + status,
          normalisedStatus: status,
        }),
      ),
    );
    expect(rows(root)).toHaveLength(statuses.length);
    rows(root).forEach((attempt, index) => {
      expect(attempt.querySelector('[data-attempt-raw]')?.textContent).toContain(
        'raw-' + statuses[index],
      );
      expect(attempt.querySelector('[data-attempt-normalised]')?.textContent).toContain(
        statuses[index],
      );
    });
  });

  it('renders unknown statuses verbatim with explicit unavailable copy and no success', async () => {
    const { root } = await render([row({ normalisedStatus: 'PASSED-new\u202e<script>' })]);
    const status = root.querySelector('[data-attempt-normalised]')!;
    expect(status.textContent).toContain('Status unavailable');
    expect(status.querySelector('bdi')?.textContent).toBe('PASSED-new⟨U+202E⟩<script>');
    expect(root.querySelector('.status-pill--success')).toBeNull();
    expect(root.querySelector('script')).toBeNull();
  });

  it.each([null, -1, NaN, undefined, Infinity])(
    'renders unavailable, never zero, for duration %s',
    async (duration) => {
      const attempt = { ...row(), durationMs: duration } as AttemptRow;
      const { root } = await render([attempt]);
      const cell = root.querySelector('[data-attempt-duration]')!;
      expect(cell.textContent?.trim()).toBe('Status unavailable');
      expect(cell.textContent).not.toContain('0');
    },
  );
  it.each([
    [0, '0 ms'],
    [320, '320 ms'],
    [999, '999 ms'],
    [1000, '1 s'],
    [1250, '1.25 s'],
    [61000, '61 s'],
  ] as const)(
    'formats duration %s as %s with the same formatter',
    async (durationMs, formatted) => {
      const { root } = await render([row({ durationMs })]);
      expect(root.querySelector('[data-attempt-duration]')?.textContent?.trim()).toBe(formatted);
    },
  );
  it.each([null, undefined, NaN, -3, Infinity, 1.5])(
    'renders unavailable artifacts for invalid count %s, distinct from known zero',
    async (artifactCount) => {
      const attempt = { ...row(), artifactCount } as AttemptRow;
      const { root } = await render([attempt, row({ attemptNo: 2, artifactCount: 0 })]);
      const cells = root.querySelectorAll('[data-attempt-artifacts]');
      expect(cells[0]?.textContent?.trim()).toBe('Status unavailable');
      expect(cells[1]?.textContent?.trim()).toBe('0');
    },
  );
  it('renders a known positive artifact count without an unavailable pill', async () => {
    const { root } = await render([row({ artifactCount: 2 })]);
    const cell = root.querySelector('[data-attempt-artifacts]')!;
    expect(cell.textContent?.trim()).toBe('2');
    expect(cell.querySelector('app-status-pill')).toBeNull();
  });

  it('shows fail then pass in attempt order despite out-of-order input, with no healing pin', async () => {
    const { root } = await render([
      row({ attemptNo: 2, rawStatus: 'ok', normalisedStatus: 'passed' }),
      row({ attemptNo: 1, rawStatus: 'assertion-failed' }),
    ]);
    expect(
      rows(root).map((attempt) => attempt.querySelector('.attempt-number')?.textContent?.trim()),
    ).toEqual(['Attempt 1', 'Attempt 2']);
    expect(
      rows(root).map((attempt) => attempt.querySelector('[data-attempt-raw] bdi')?.textContent),
    ).toEqual(['assertion-failed', 'ok']);
    expect(root.querySelector('[data-pre-healing]')).toBeNull();
    expect(root.querySelector('[data-healing-candidate]')).toBeNull();
  });

  it.each(['shadow', 'official'] as const)(
    'keeps pre-healing failure and %s candidate in distinct blocks, not a flaky verdict',
    async (mode) => {
      const { root } = await render([
        row({
          attemptNo: 3,
          normalisedStatus: 'passed',
          healingCandidate: { candidateHash: 'sha256:candidate', mode },
        }),
        row({ attemptNo: 1, normalisedStatus: 'skipped' }),
        row({ attemptNo: 2, rawStatus: 'original-error' }),
      ]);
      const attempts = rows(root);
      expect(attempts).toHaveLength(3);
      expect(attempts[0]?.querySelector('.attempt-number')?.textContent).toBe('Attempt 2');
      expect(attempts[0]?.querySelector('.pre-healing-block')?.textContent).toContain(
        'Pre-healing failure',
      );
      expect(attempts[0]?.querySelector('[data-attempt-raw]')?.textContent).toContain(
        'original-error',
      );
      const candidate = root.querySelector('[data-healing-candidate]')!;
      expect(candidate.querySelector('bdi')?.textContent).toBe('sha256:candidate');
      expect(candidate.textContent).toContain(
        mode === 'shadow' ? 'Shadow evaluation' : 'Official evaluation',
      );
      expect(candidate.querySelector('.pre-healing-block')).toBeNull();
      expect(root.textContent).not.toMatch(/flaky/i);
      expect(root.querySelector('app-decision-chip, app-outcome-pill')).toBeNull();
    },
  );

  it('escapes format characters and isolates every supplied string without parsing markup', async () => {
    const { root } = await render([
      row({
        executionKey: 'exec\u202e<script>',
        rawStatus: 'raw\u202e<script>',
        normalisedStatus: 'unknown\u202e<script>',
        healingCandidate: { candidateHash: 'hash\u202e<script>', mode: 'shadow' },
      }),
    ]);
    expect(root.querySelector('th[scope="row"] > bdi')?.textContent).toBe('exec⟨U+202E⟩<script>');
    expect(root.querySelector('[data-attempt-raw] bdi')?.textContent).toBe('raw⟨U+202E⟩<script>');
    expect(root.querySelector('[data-attempt-normalised] > bdi')?.textContent).toBe(
      'unknown⟨U+202E⟩<script>',
    );
    expect(root.querySelector('[data-healing-candidate] bdi')?.textContent).toBe(
      'hash⟨U+202E⟩<script>',
    );
    expect(root.textContent).not.toContain('\u202e');
    expect(root.querySelector('script')).toBeNull();
  });

  it('renders null as shared unavailable, not empty or a table', async () => {
    const { root } = await render(null);
    expect(
      root.querySelector('[data-attempts-unavailable] app-status-pill')?.textContent,
    ).toContain('Status unavailable');
    expect(root.querySelector('app-empty-pattern, table')).toBeNull();
  });
  it('renders an empty array as the shared empty state, not unavailable', async () => {
    const { root } = await render([]);
    expect(root.querySelector('app-empty-pattern')?.textContent).toContain('No attempts recorded');
    expect(root.querySelector('[data-attempts-unavailable], table')).toBeNull();
  });
  it('renders the healing marker as the shared unavailable state while no row has a candidate', async () => {
    const { root } = await render([row()]);
    expect(
      root.querySelector('[data-attempts-healing-unavailable] app-status-pill')?.textContent,
    ).toContain('Status unavailable');
    expect(root.querySelector('[data-pre-healing], [data-healing-candidate]')).toBeNull();
  });
  it('renders loading and a retryable first-page failure instead of a table or unavailable', async () => {
    const { root, fixture } = await render(null);
    fixture.componentRef.setInput('status', 'loading');
    await fixture.whenStable();
    expect(root.querySelector('app-loading-pattern')).not.toBeNull();
    expect(root.querySelector('table, [data-attempts-unavailable]')).toBeNull();
    fixture.componentRef.setInput('status', 'error');
    fixture.componentRef.setInput('error', {
      kind: 'unavailable',
      status: 503,
      code: 'unavailable',
      message: '',
      requestId: null,
      retryable: true,
      details: [],
      retryAfterSeconds: null,
    });
    await fixture.whenStable();
    const retried = vi.fn();
    fixture.componentInstance.retry.subscribe(retried);
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    root.querySelector<HTMLButtonElement>('app-error-pattern button')!.click();
    expect(retried).toHaveBeenCalledTimes(1);
  });
  it('offers one bounded load-more only while a cursor remains and keeps the page on append', async () => {
    const input = Array.from({ length: 120 }, (_, index) =>
      row({ executionKey: 'execution-' + index }),
    );
    const { root, fixture } = await render(input.slice(0, 100));
    const more = vi.fn();
    fixture.componentInstance.loadMore.subscribe(more);
    expect(root.querySelector('[data-attempts-load-more]')).toBeNull();
    fixture.componentRef.setInput('hasMore', true);
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('[data-attempts-next]')!.click();
    await fixture.whenStable();
    root.querySelector<HTMLButtonElement>('[data-attempts-load-more]')!.click();
    expect(more).toHaveBeenCalledTimes(1);
    fixture.componentRef.setInput('attempts', [...input]);
    fixture.componentRef.setInput('hasMore', false);
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Page 2 / 3');
    expect(root.querySelector('[data-attempts-load-more]')).toBeNull();
  });
  it('shows a failed follow-up page beside the rows already loaded', async () => {
    const { root, fixture } = await render([row()]);
    fixture.componentRef.setInput('hasMore', true);
    fixture.componentRef.setInput('error', {
      kind: 'network',
      status: 0,
      code: '',
      message: '',
      requestId: null,
      retryable: true,
      details: [],
      retryAfterSeconds: null,
    });
    await fixture.whenStable();
    expect(rows(root)).toHaveLength(1);
    expect(root.querySelector('app-error-pattern')).not.toBeNull();
    expect(root.querySelector('[data-attempts-load-more]')).not.toBeNull();
  });

  describe('follow-up focus and announcements', () => {
    const networkFailure: ApiError = {
      kind: 'network',
      status: 0,
      code: '',
      message: '',
      requestId: null,
      retryable: true,
      details: [],
      retryAfterSeconds: null,
    };

    it('moves focus to the pager reach when the last page removes a focused Load more', async () => {
      const input = Array.from({ length: 120 }, (_, index) =>
        row({ executionKey: `execution-${index}` }),
      );
      const { root, fixture } = await render(input.slice(0, 100));
      fixture.componentRef.setInput('hasMore', true);
      await fixture.whenStable();
      const more = root.querySelector<HTMLButtonElement>('[data-attempts-load-more]')!;
      const loaded = vi.fn();
      fixture.componentInstance.loadMore.subscribe(loaded);
      more.focus();
      more.click();
      expect(loaded).toHaveBeenCalledTimes(1);
      fixture.componentRef.setInput('loadingMore', true);
      await fixture.whenStable();
      // The read settles as the last page: the focused control is gone, so focus must not fall
      // to `body` (WCAG 2.4.3) — it moves to the pager's forward reach.
      fixture.componentRef.setInput('attempts', [...input]);
      fixture.componentRef.setInput('hasMore', false);
      fixture.componentRef.setInput('loadingMore', false);
      await fixture.whenStable();
      expect(root.querySelector('[data-attempts-load-more]')).toBeNull();
      const next = root.querySelector<HTMLButtonElement>('[data-attempts-next]')!;
      expect(next.disabled).toBe(false);
      expect(document.activeElement).toBe(next);
    });

    it('moves focus to the table region when the last page leaves no pager behind', async () => {
      const first = Array.from({ length: 40 }, (_, index) =>
        row({ executionKey: `execution-${index}` }),
      );
      const appended = Array.from({ length: 5 }, (_, index) =>
        row({ executionKey: `later-${index}` }),
      );
      const { root, fixture } = await render(first);
      fixture.componentRef.setInput('hasMore', true);
      await fixture.whenStable();
      const more = root.querySelector<HTMLButtonElement>('[data-attempts-load-more]')!;
      more.focus();
      more.click();
      fixture.componentRef.setInput('attempts', [...first, ...appended]);
      fixture.componentRef.setInput('hasMore', false);
      await fixture.whenStable();
      expect(root.querySelector('[data-attempts-load-more]')).toBeNull();
      expect(root.querySelector('nav')).toBeNull();
      expect(document.activeElement).toBe(root.querySelector('.table-scroll'));
    });

    it('leaves focus where the visitor moved while the last page was in flight', async () => {
      const input = Array.from({ length: 120 }, (_, index) =>
        row({ executionKey: `execution-${index}` }),
      );
      const { root, fixture } = await render(input.slice(0, 100));
      fixture.componentRef.setInput('hasMore', true);
      await fixture.whenStable();
      const more = root.querySelector<HTMLButtonElement>('[data-attempts-load-more]')!;
      more.click();
      const elsewhere = document.createElement('button');
      document.body.append(elsewhere);
      try {
        // The visitor tabbed out of the panel while the read was in flight.
        elsewhere.focus();
        fixture.componentRef.setInput('loadingMore', true);
        await fixture.whenStable();
        fixture.componentRef.setInput('attempts', [...input]);
        fixture.componentRef.setInput('hasMore', false);
        fixture.componentRef.setInput('loadingMore', false);
        await fixture.whenStable();
        expect(root.querySelector('[data-attempts-load-more]')).toBeNull();
        expect(document.activeElement).toBe(elsewhere);
      } finally {
        elsewhere.remove();
      }
    });

    it('moves focus to Load more when the retry clears a focused follow-up failure', async () => {
      const { root, fixture } = await render([row()]);
      fixture.componentRef.setInput('hasMore', true);
      fixture.componentRef.setInput('error', networkFailure);
      await fixture.whenStable();
      const retry = root.querySelector<HTMLButtonElement>('app-error-pattern button')!;
      const retried = vi.fn();
      fixture.componentInstance.loadMore.subscribe(retried);
      retry.focus();
      retry.click();
      expect(retried).toHaveBeenCalledTimes(1);
      // The parent clears the failure and marks the retried read in flight: the block that held the
      // focused Retry is gone for the whole request, so the place moves on now, not at settle.
      fixture.componentRef.setInput('error', null);
      fixture.componentRef.setInput('loadingMore', true);
      await fixture.whenStable();
      expect(root.querySelector('app-error-pattern')).toBeNull();
      const more = root.querySelector<HTMLButtonElement>('[data-attempts-load-more]')!;
      expect(more.getAttribute('aria-busy')).toBe('true');
      expect(document.activeElement).toBe(more);
      fixture.componentRef.setInput('loadingMore', false);
      await fixture.whenStable();
      expect(document.activeElement).toBe(more);
    });

    it('moves focus to the panel heading when a first-page retry removes the focused Retry', async () => {
      const { root, fixture } = await render(null);
      fixture.componentRef.setInput('status', 'error');
      fixture.componentRef.setInput('error', networkFailure);
      await fixture.whenStable();
      const retry = root.querySelector<HTMLButtonElement>('app-error-pattern button')!;
      const retried = vi.fn();
      fixture.componentInstance.retry.subscribe(retried);
      retry.focus();
      retry.click();
      expect(retried).toHaveBeenCalledTimes(1);
      fixture.componentRef.setInput('status', 'loading');
      fixture.componentRef.setInput('error', null);
      await fixture.whenStable();
      expect(root.querySelector('app-error-pattern')).toBeNull();
      const heading = root.querySelector<HTMLElement>('#attempts-title')!;
      expect(document.activeElement).toBe(heading);
      fixture.componentRef.setInput('attempts', [row()]);
      fixture.componentRef.setInput('status', 'ready');
      await fixture.whenStable();
      expect(rows(root)).toHaveLength(1);
      expect(document.activeElement).toBe(heading);
    });

    it('marks the attempts region busy and announces progress outside the busy region', async () => {
      const { root, fixture } = await render([row()]);
      fixture.componentRef.setInput('hasMore', true);
      await fixture.whenStable();
      const region = root.querySelector<HTMLElement>('.table-scroll')!;
      const status = root.querySelector<HTMLElement>('[data-attempts-status]')!;
      expect(status.getAttribute('role')).toBe('status');
      expect(region.contains(status)).toBe(false);
      expect(region.getAttribute('aria-busy')).toBeNull();
      expect(status.textContent?.trim()).toBe('');

      fixture.componentRef.setInput('loadingMore', true);
      await fixture.whenStable();
      expect(region.getAttribute('aria-busy')).toBe('true');
      expect(root.querySelector('[data-attempts-load-more]')?.getAttribute('aria-busy')).toBe(
        'true',
      );
      expect(status.textContent?.trim()).toBe('Loading more attempts');
      expect(region.contains(status)).toBe(false);

      fixture.componentRef.setInput('attempts', [row(), row({ attemptNo: 2 })]);
      fixture.componentRef.setInput('loadingMore', false);
      await fixture.whenStable();
      expect(region.getAttribute('aria-busy')).toBeNull();
      expect(status.textContent?.trim()).toBe('More attempts loaded');
    });

    it('announces no completion when the follow-up page fails', async () => {
      const { root, fixture } = await render([row()]);
      fixture.componentRef.setInput('hasMore', true);
      fixture.componentRef.setInput('loadingMore', true);
      await fixture.whenStable();
      const status = root.querySelector<HTMLElement>('[data-attempts-status]')!;
      expect(status.textContent?.trim()).toBe('Loading more attempts');
      fixture.componentRef.setInput('error', networkFailure);
      fixture.componentRef.setInput('loadingMore', false);
      await fixture.whenStable();
      expect(root.querySelector('app-error-pattern')).not.toBeNull();
      expect(status.textContent?.trim()).toBe('');
    });

    it.each(['loading', 'error', 'unavailable', 'empty', 'ready'] as const)(
      'keeps the follow-up live region mounted in the %s phase',
      async (phase) => {
        const { root, fixture } = await render(
          phase === 'ready' ? [row()] : phase === 'empty' ? [] : null,
        );
        fixture.componentRef.setInput(
          'status',
          phase === 'error' ? 'error' : phase === 'loading' ? 'loading' : 'ready',
        );
        if (phase === 'error') fixture.componentRef.setInput('error', networkFailure);
        await fixture.whenStable();
        const marker = {
          loading: 'app-loading-pattern',
          error: 'app-error-pattern',
          unavailable: '[data-attempts-unavailable]',
          empty: 'app-empty-pattern',
          ready: 'table',
        }[phase];
        expect(root.querySelector(marker)).not.toBeNull();
        const status = root.querySelector<HTMLElement>('[data-attempts-status]');
        expect(status?.getAttribute('role')).toBe('status');
        expect(status?.closest('section')).not.toBeNull();
      },
    );
  });
  it('uses a caption and scoped column/row headers', async () => {
    const { root } = await render([row()]);
    expect(root.querySelector('caption')?.textContent?.trim()).toBe('Attempts');
    expect(root.querySelectorAll('thead th[scope="col"]')).toHaveLength(5);
    expect(root.querySelectorAll('tbody th[scope="row"]')).toHaveLength(1);
    expect(root.querySelector('.table-scroll')?.getAttribute('tabindex')).toBe('0');
    expect(root.querySelector('section')?.getAttribute('aria-labelledby')).toBe('attempts-title');
    expect(root.querySelector('#attempts-title')?.textContent).toBe('Attempts');
    expect(root.querySelector('section')?.hasAttribute('aria-label')).toBe(false);
    expect(root.querySelector('[role="region"]')?.getAttribute('aria-label')).toBe(
      'Attempts table',
    );
  });

  it('bounds 120 execution groups to 50 rows, pages both ways and resets on new input', async () => {
    const input = Array.from({ length: 120 }, (_, index) =>
      row({ executionKey: `execution-${index}` }),
    );
    const { root, fixture } = await render(input);
    const next = () => root.querySelector<HTMLButtonElement>('[data-attempts-next]')!;
    const previous = () => root.querySelector<HTMLButtonElement>('[data-attempts-previous]')!;
    expect(rows(root)).toHaveLength(50);
    expect(root.querySelector('nav')?.getAttribute('aria-label')).toBe('Attempts pagination');
    expect(root.querySelector('[role="status"]')?.hasAttribute('aria-live')).toBe(false);
    expect(previous().disabled).toBe(true);
    next().click();
    await fixture.whenStable();
    expect(rows(root)).toHaveLength(50);
    expect(rows(root)[0]?.textContent).toContain('execution-50');
    next().click();
    await fixture.whenStable();
    expect(rows(root)).toHaveLength(20);
    expect(next().disabled).toBe(true);
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Page 3 / 3');
    previous().click();
    await fixture.whenStable();
    expect(rows(root)[0]?.textContent).toContain('execution-50');
    fixture.componentRef.setInput('attempts', [...input]);
    await fixture.whenStable();
    expect(rows(root)[0]?.textContent).toContain('execution-0');
    expect(previous().disabled).toBe(true);
  });
  it('hands focus to the opposite pager button when a page turn disables the activated one', async () => {
    const input = Array.from({ length: 120 }, (_, index) =>
      row({ executionKey: `execution-${index}` }),
    );
    const { root, fixture } = await render(input);
    const next = () => root.querySelector<HTMLButtonElement>('[data-attempts-next]')!;
    const previous = () => root.querySelector<HTMLButtonElement>('[data-attempts-previous]')!;

    // Reaching the last page disables Next in the render that follows its own activation, so a
    // disabled element cannot keep holding focus: the place moves to the pager's other control.
    next().focus();
    next().click();
    await fixture.whenStable();
    next().click();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Page 3 / 3');
    expect(next().disabled).toBe(true);
    expect(document.activeElement).toBe(previous());

    // The same on the way back: Previous disables itself at page 1 and Next takes the focus.
    previous().click();
    await fixture.whenStable();
    previous().click();
    await fixture.whenStable();
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Page 1 / 3');
    expect(previous().disabled).toBe(true);
    expect(document.activeElement).toBe(next());
  });

  it('leaves focus alone when the activated pager button never held it', async () => {
    const input = Array.from({ length: 120 }, (_, index) =>
      row({ executionKey: `execution-${index}` }),
    );
    const { root, fixture } = await render(input);
    // A pointer press that did not move focus must not move it afterwards either.
    expect(document.activeElement).toBe(document.body);
    for (let page = 0; page < 2; page++) {
      root.querySelector<HTMLButtonElement>('[data-attempts-next]')!.click();
      await fixture.whenStable();
    }
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Page 3 / 3');
    expect(document.activeElement).toBe(document.body);
  });

  it('resets synchronously when a later page shrinks to fewer pages or a single row', async () => {
    const input = Array.from({ length: 120 }, (_, index) =>
      row({ executionKey: `execution-${index}` }),
    );
    const { root, fixture } = await render(input);
    for (let page = 0; page < 2; page++) {
      root.querySelector<HTMLButtonElement>('[data-attempts-next]')!.click();
      await fixture.whenStable();
    }
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Page 3 / 3');

    fixture.componentRef.setInput('attempts', input.slice(0, 51));
    fixture.detectChanges();
    expect(rows(root)).toHaveLength(50);
    expect(rows(root)[0]?.textContent).toContain('execution-0');
    expect(root.querySelector('[role="status"]')?.textContent).toContain('Page 1 / 2');

    root.querySelector<HTMLButtonElement>('[data-attempts-next]')!.click();
    await fixture.whenStable();
    fixture.componentRef.setInput('attempts', input.slice(0, 1));
    fixture.detectChanges();
    expect(rows(root)).toHaveLength(1);
    expect(rows(root)[0]?.textContent).toContain('execution-0');
    expect(root.querySelector('nav')).toBeNull();
  });
  it('bounds a 5000-attempt healing chain and keeps the original failure pinned on continuation', async () => {
    const input = Array.from({ length: 5000 }, (_, index) =>
      row({
        attemptNo: index + 1,
        normalisedStatus: index === 0 ? 'failed' : 'passed',
        healingCandidate: index === 1 ? { candidateHash: 'candidate', mode: 'shadow' } : null,
      }),
    );
    const { root, fixture } = await render(input);
    expect(rows(root)).toHaveLength(50);
    root.querySelector<HTMLButtonElement>('[data-attempts-next]')!.click();
    await fixture.whenStable();
    expect(rows(root)).toHaveLength(50);
    expect(rows(root)[0]?.querySelector('.attempt-number')?.textContent).toBe('Attempt 1');
    expect(rows(root)[0]?.querySelector('.pre-healing-block')?.textContent).toContain(
      'Pre-healing failure',
    );
    expect(rows(root)[1]?.querySelector('.attempt-number')?.textContent).toBe('Attempt 51');
  });
});
