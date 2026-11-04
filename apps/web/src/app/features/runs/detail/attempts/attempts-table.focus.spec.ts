import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import type { ApiError } from '../../../../core/http/api-error';
import { AttemptsTableComponent } from './attempts-table.component';
import { render, requiredElement, row, rows } from './attempts-table.test-support';

describe('AttemptsTableComponent focus', () => {
  beforeEach(() =>
    TestBed.configureTestingModule({
      imports: [AttemptsTableComponent],
      providers: [provideZonelessChangeDetection()],
    }),
  );
  afterEach(() => TestBed.resetTestingModule());
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
      const more = requiredElement<HTMLButtonElement>(root, '[data-attempts-load-more]');
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
      const next = requiredElement<HTMLButtonElement>(root, '[data-attempts-next]');
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
      const more = requiredElement<HTMLButtonElement>(root, '[data-attempts-load-more]');
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
      const more = requiredElement<HTMLButtonElement>(root, '[data-attempts-load-more]');
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
      const retry = requiredElement<HTMLButtonElement>(root, 'app-error-pattern button');
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
      const more = requiredElement<HTMLButtonElement>(root, '[data-attempts-load-more]');
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
      const retry = requiredElement<HTMLButtonElement>(root, 'app-error-pattern button');
      const retried = vi.fn();
      fixture.componentInstance.retry.subscribe(retried);
      retry.focus();
      retry.click();
      expect(retried).toHaveBeenCalledTimes(1);
      fixture.componentRef.setInput('status', 'loading');
      fixture.componentRef.setInput('error', null);
      await fixture.whenStable();
      expect(root.querySelector('app-error-pattern')).toBeNull();
      const heading = requiredElement(root, '#attempts-title');
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
      const region = requiredElement(root, '.table-scroll');
      const status = requiredElement(root, '[data-attempts-status]');
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
      const status = requiredElement(root, '[data-attempts-status]');
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
});
