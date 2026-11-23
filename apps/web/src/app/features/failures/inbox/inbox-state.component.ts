import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import type { ApiError } from '../../../core/http/api-error';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { RetryAfterComponent } from '../../../shared/ui/conflict';
import {
  EmptyPatternComponent,
  ErrorPatternComponent,
  ForbiddenPatternComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import { INBOX_COPY as c } from './failure-inbox.copy';
import { INCIDENTS, ORIGINAL, RUN_EVIDENCE } from './failure-inbox.fixture';
import { FailureInboxStore, type InboxState } from './failure-inbox.store';

const descriptions: Partial<Record<InboxState, readonly [string, string]>> = {
  '401': [c.expired, c.expiredText],
  '412': [c.conflict, c.conflictText],
  collect: [c.collect, c.collectText],
  closed: [c.closed, c.closedText],
  model: [c.model, c.modelText],
  submitting: [c.submitting, c.submittingText],
  saved: [c.saved, c.savedText],
  latest: [c.latest, c.latestText],
};

@Component({
  selector: 'app-inbox-state',
  imports: [
    AbsoluteTimeComponent,
    EmptyPatternComponent,
    ErrorPatternComponent,
    ForbiddenPatternComponent,
    HlmButton,
    HlmCard,
    HlmIcon,
    LoadingPatternComponent,
    NotFoundPatternComponent,
    RetryAfterComponent,
    RouterLink,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @switch (store.state()) {
      @case ('loading') {
        <app-loading-pattern [layout]="'cards'" [label]="c.loading" />
      }
      @case ('empty') {
        <app-empty-pattern [icon]="'navInbox'" [title]="c.empty" [description]="c.emptyText" />
      }
      @case ('nomatch') {
        <app-empty-pattern
          [icon]="'navInbox'"
          [title]="c.noMatch"
          [description]="c.noMatchText"
          [actionLabel]="c.clear"
          (action)="store.clear()"
        />
      }
      @case ('error') {
        <app-error-pattern [error]="error" (retry)="store.recover()" />
      }
      @case ('403') {
        <app-forbidden-pattern />
        <p class="text-sm text-muted-foreground">{{ c.deniedText }}</p>
      }
      @case ('404') {
        <app-not-found-pattern />
      }
      @case ('429') {
        <app-retry-after
          [retryAfterSeconds]="60"
          [requestId]="'req-901'"
          [retryEligible]="store.retryEligible()"
          (retry)="store.recover()"
        />
      }
      @case ('snapshot') {
        <section hlmCard class="grid gap-2 p-4" role="status">
          <h2 class="m-0 text-lg">{{ c.snapshot }}</h2>
          @for (row of captures; track row.id) {
            <p class="m-0">
              {{ row.id }} · <app-absolute-time [value]="row.captured" /> ·
              {{ row.id === 'inc-91' ? original : row.caseRef }} · {{ row.title }}
            </p>
          }
        </section>
      }
      @default {
        @if (notice(); as message) {
          <section hlmCard class="grid justify-items-start gap-2 p-4" role="status">
            <h2 class="m-0 flex items-center gap-2 text-lg font-bold">
              <hlm-icon
                [name]="
                  store.state() === 'submitting' || store.state() === 'collect'
                    ? 'lucideClock'
                    : 'lucideInfo'
                "
              />{{ message[0] }}
            </h2>
            <p class="m-0 text-muted-foreground">{{ message[1] }}</p>
            @switch (store.state()) {
              @case ('401') {
                <a
                  hlmBtn
                  [variant]="'outline'"
                  routerLink="/login"
                  [queryParams]="{ returnUrl: store.returnUrl() }"
                  >{{ c.signIn }}</a
                >
              }
              @case ('412') {
                <button
                  hlmBtn
                  [variant]="'outline'"
                  type="button"
                  (click)="store.recover('latest')"
                >
                  {{ c.reload }}
                </button>
              }
              @case ('closed') {
                <a
                  hlmBtn
                  [variant]="'outline'"
                  [routerLink]="store.path('failure-detail')"
                  [queryParams]="{ id: 'inc-91' }"
                  >{{ c.attempt }}</a
                >
              }
              @case ('collect') {
                <details>
                  <summary class="cursor-pointer">{{ c.producer }}</summary>
                  <p>{{ producer }}</p>
                </details>
              }
            }
          </section>
        }
      }
    }
  `,
})
export class InboxStateComponent {
  protected readonly store = inject(FailureInboxStore);
  protected readonly c = c;
  protected readonly original = ORIGINAL;
  protected readonly producer = RUN_EVIDENCE.producer;
  protected readonly captures = INCIDENTS.filter((row) => row.captured);
  protected readonly notice = computed(() => descriptions[this.store.state()]);
  protected readonly error: ApiError = {
    kind: 'unexpected',
    status: 500,
    code: 'request_failed',
    message: c.errorText,
    requestId: 'req-901',
    retryable: true,
    retryAfterSeconds: null,
    details: [],
  };
}
