import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  viewChild,
} from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { provideRouter, RouterOutlet } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import type { RunAttemptPage } from '@evidgate/sdk';
import { ApiRequestError } from '../../../core/http/api-error';
import { provideLayoutTestStubs } from '../../../shared/testing/layout.testing';
import { routes } from '../runs.routes';
import { attemptAnchorId } from './evidence/run-evidence-anchors';
import { deferred } from './run-detail-store.fixtures';
import { RunDetailPage } from './run-detail.page';
import {
  attemptPage,
  configureRunDetailTestBed,
  get,
  listAttempts,
  listEvidenceRevisions,
  open,
  run,
  settle,
} from './run-detail.page.test-support';

class AnchorPagingHarness extends ComponentHarness {
  static hostSelector = 'app-run-detail-page';
  readonly previous = this.locatorFor('app-attempts-table [data-page="previous"]');
  readonly summary = this.locatorFor('app-attempts-table app-data-table-pager [role="status"]');
  readonly rows = this.locatorForAll('app-attempts-table [data-attempt-row]');
  readonly producers = this.locatorFor('app-producer-status-table');
  readonly producerNext = this.locatorFor('app-producer-status-table [data-page="next"]');
  readonly producerSummary = this.locatorFor(
    'app-producer-status-table app-data-table-pager [role="status"]',
  );
  readonly retry = this.locatorFor('app-attempts-table app-error-pattern button');
  readonly back = this.locatorFor('a');
}

@Component({
  imports: [RouterOutlet],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: '<main #main tabindex="-1"><router-outlet /></main>',
})
class AnchorShell {
  private readonly main = viewChild.required<ElementRef<HTMLElement>>('main');

  constructor() {
    afterNextRender(() => this.main().nativeElement.focus());
  }
}

function page(start: number, next: string | null): RunAttemptPage {
  const base = attemptPage.items[0];
  if (!base) throw new Error('An attempt fixture is required');
  return {
    items: Array.from({ length: 20 }, (_, index) => ({
      ...base,
      execution_key: `anchor-${start + index}`,
      attempt_no: 1,
    })),
    next_cursor: next,
  };
}
const anchor = (index: number) => attemptAnchorId(`anchor-${index}`, 1);
const url = (fragment: string, id = run.run_id) => `/projects/project-1/runs/${id}#${fragment}`;

describe('RunDetailPage attempt anchor paging', () => {
  provideLayoutTestStubs();
  beforeEach(() => configureRunDetailTestBed());
  afterEach(() => TestBed.resetTestingModule());

  it.each([false, true])(
    'accepts the initial shell main handoff but respects later keyboard movement: %s',
    async (move) => {
      TestBed.configureTestingModule({
        providers: [
          provideRouter([
            {
              path: 'wrapped/projects/:projectId/runs',
              component: AnchorShell,
              children: routes,
            },
          ]),
        ],
      });
      const first = deferred<RunAttemptPage>();
      listAttempts.mockReturnValueOnce(first.promise).mockResolvedValueOnce(page(21, null));
      const harness = await RouterTestingHarness.create();
      await harness.navigateByUrl(`/wrapped/projects/project-1/runs/${run.run_id}#${anchor(21)}`);
      await settle(harness);
      expect(document.activeElement?.tagName).toBe('MAIN');
      if (move) {
        document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true }));
      }
      first.resolve(page(1, 'second'));
      await settle(harness);
      expect(document.activeElement?.id).toBe(move ? '' : anchor(21));
      expect(listAttempts).toHaveBeenCalledTimes(move ? 1 : 2);
    },
  );

  it('lands target 21 with bounded rows and preserves Previous without re-landing', async () => {
    listAttempts
      .mockResolvedValueOnce(page(1, 'second'))
      .mockResolvedValueOnce(page(21, 'third'))
      .mockResolvedValueOnce(page(1, 'second'));
    const { harness } = await open(run.run_id, anchor(21));
    const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      AnchorPagingHarness,
    );
    expect(document.activeElement?.id).toBe(anchor(21));
    expect(await table.rows()).toHaveLength(20);
    expect(await (await table.summary()).text()).toBe('Page 2');
    expect(listAttempts).toHaveBeenNthCalledWith(2, run.run_id, { limit: 20, cursor: 'second' });
    await (await table.previous()).click();
    await settle(harness);
    expect(await (await table.summary()).text()).toBe('Page 1');
    expect(listAttempts).toHaveBeenCalledTimes(3);
    expect(document.activeElement?.id).not.toBe(anchor(21));
    expect(listEvidenceRevisions).toHaveBeenCalledTimes(1);
  });
  it('lands a target on page three without preloading a fourth page', async () => {
    listAttempts
      .mockResolvedValueOnce(page(1, 'second'))
      .mockResolvedValueOnce(page(21, 'third'))
      .mockResolvedValueOnce(page(41, 'fourth'));
    const { harness } = await open(run.run_id, anchor(41));
    const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      AnchorPagingHarness,
    );
    await settle(harness);
    expect(document.activeElement?.id).toBe(anchor(41));
    expect(await (await table.summary()).text()).toBe('Page 3');
    expect(await table.rows()).toHaveLength(20);
    expect(listAttempts).toHaveBeenCalledTimes(3);
  });

  it('walks to page three only for an anchor, then stops at the collection end when missing', async () => {
    listAttempts
      .mockResolvedValueOnce(page(1, 'second'))
      .mockResolvedValueOnce(page(21, 'third'))
      .mockResolvedValueOnce(page(41, null));
    const { harness } = await open(run.run_id, anchor(80));
    const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      AnchorPagingHarness,
    );
    await settle(harness);
    expect(await (await table.summary()).text()).toBe('Page 3');
    expect(await table.rows()).toHaveLength(20);
    expect(listAttempts).toHaveBeenCalledTimes(3);
    expect(document.activeElement?.id).not.toBe(anchor(80));
  });

  it.each(['', 'attempt-invalid-0', '%E0%A4%A'])(
    'does not preload for fragment %s',
    async (fragment) => {
      listAttempts.mockResolvedValue(page(1, 'second'));
      const { harness } = await open(run.run_id, encodeURIComponent(fragment));
      await settle(harness);
      expect(listAttempts).toHaveBeenCalledExactlyOnceWith(run.run_id, { limit: 20 });
    },
  );

  it('refuses a cursor cycle without retrying it automatically', async () => {
    listAttempts
      .mockResolvedValueOnce(page(1, 'second'))
      .mockResolvedValueOnce(page(21, 'third'))
      .mockResolvedValueOnce(page(41, 'second'));
    const { harness } = await open(run.run_id, anchor(80));
    const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      AnchorPagingHarness,
    );
    await settle(harness);
    expect(listAttempts).toHaveBeenCalledTimes(3);
    expect(await (await table.summary()).text()).toBe('Page 2');
    expect(await table.rows()).toHaveLength(20);
    expect(document.activeElement?.id).not.toBe(anchor(80));
  });

  it.each(['forbidden', 'unavailable'] as const)(
    'stops on %s and preserves explicit latest-page retry',
    async (kind) => {
      listAttempts
        .mockResolvedValueOnce(page(1, 'second'))
        .mockRejectedValueOnce(
          new ApiRequestError({
            kind,
            status: kind === 'forbidden' ? 403 : 503,
            code: kind,
            message: '',
            requestId: null,
            retryable: kind === 'unavailable',
            details: [],
            retryAfterSeconds: null,
          }),
        )
        .mockResolvedValueOnce(page(21, null));
      const { harness } = await open(run.run_id, anchor(21));
      const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
        AnchorPagingHarness,
      );
      await settle(harness);
      expect(listAttempts).toHaveBeenCalledTimes(2);
      expect(await (await table.summary()).text()).toBe('Page 1');
      expect(document.activeElement?.id).not.toBe(anchor(21));
      if (kind === 'unavailable') {
        await (await table.retry()).click();
        await settle(harness);
        expect(listAttempts).toHaveBeenLastCalledWith(run.run_id, { limit: 20, cursor: 'second' });
        expect(await (await table.summary()).text()).toBe('Page 2');
      }
    },
  );

  it('cancels an old fragment walk and lands a new earlier-page target', async () => {
    const pending = deferred<RunAttemptPage>();
    listAttempts
      .mockResolvedValueOnce(page(1, 'second'))
      .mockReturnValueOnce(pending.promise)
      .mockResolvedValueOnce(page(1, 'second'));
    const { harness } = await open(run.run_id, anchor(60));
    await harness.navigateByUrl(url(anchor(1)), RunDetailPage);
    pending.resolve(page(21, 'third'));
    await settle(harness);
    expect(document.activeElement?.id).toBe(anchor(1));
    expect(listAttempts).not.toHaveBeenCalledWith(run.run_id, { limit: 20, cursor: 'third' });
  });

  it('ignores a stale run response and lands the same fragment in the new run', async () => {
    const pending = deferred<RunAttemptPage>();
    listAttempts
      .mockResolvedValueOnce(page(1, 'second'))
      .mockReturnValueOnce(pending.promise)
      .mockResolvedValueOnce(page(21, null));
    const { harness } = await open(run.run_id, anchor(21));
    get.mockResolvedValue({ ...run, run_id: 'new-run' });
    await harness.navigateByUrl(url(anchor(21), 'new-run'), RunDetailPage);
    await settle(harness);
    pending.resolve(page(41, 'stale-third'));
    await settle(harness);
    expect(document.activeElement?.id).toBe(anchor(21));
    expect(listAttempts).toHaveBeenLastCalledWith('new-run', { limit: 20 });
    expect(listAttempts).toHaveBeenCalledTimes(3);
  });

  it.each([21, 41])(
    'does not steal focus or continue walking after the visitor moves focus for target %s',
    async (target) => {
      const pending = deferred<RunAttemptPage>();
      listAttempts.mockResolvedValueOnce(page(1, 'second')).mockReturnValueOnce(pending.promise);
      const { harness } = await open(run.run_id, anchor(target));
      const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
        AnchorPagingHarness,
      );
      const back = await table.back();
      await back.focus();
      pending.resolve(page(21, 'third'));
      await settle(harness);
      expect(await back.isFocused()).toBe(true);
      expect(listAttempts).toHaveBeenCalledTimes(2);
    },
  );

  it('switches to a producer anchor without continuing the attempt walk or changing producer paging', async () => {
    get.mockResolvedValue({
      ...run,
      declared_producers: Array.from({ length: 60 }, (_, index) => `shard-${index + 1}`),
      producers: [],
    });
    const pending = deferred<RunAttemptPage>();
    listAttempts.mockResolvedValueOnce(page(1, 'second')).mockReturnValueOnce(pending.promise);
    const { harness } = await open(run.run_id, anchor(60));
    await harness.navigateByUrl(url('producer-shard-21'), RunDetailPage);
    await settle(harness);
    expect(document.activeElement?.id).toBe('producer-shard-21');
    const table = await TestbedHarnessEnvironment.loader(harness.fixture).getHarness(
      AnchorPagingHarness,
    );
    await (await table.producerNext()).click();
    pending.resolve(page(21, 'third'));
    await settle(harness);
    expect(await (await table.producerSummary()).text()).toBe('41–60 of 60');
    expect(listAttempts).toHaveBeenCalledTimes(2);
    expect(listEvidenceRevisions).toHaveBeenCalledTimes(1);
  });
});
