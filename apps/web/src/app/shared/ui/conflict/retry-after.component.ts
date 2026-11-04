import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';

@Component({
  selector: 'app-retry-after',
  imports: [HlmAlertImports, HlmButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section hlmAlert [attr.role]="'group'" class="retry-panel grid gap-3 wrap-anywhere">
      @if (kind() === 'unavailable') {
        <h2 hlmAlertTitle i18n="@@shared.retry-after.unavailable-title">
          503 · Service unavailable
        </h2>
      } @else {
        <h2 hlmAlertTitle i18n="@@shared.retry-after.title">429 · Rate limited</h2>
      }
      <p
        hlmAlertDescription
        class="m-0"
        role="status"
        [attr.aria-live]="'polite'"
        [attr.aria-atomic]="'true'"
      >
        @let seconds = interval();
        @if (seconds !== null) {
          <span i18n="@@shared.retry-after.interval">Retry after: {{ seconds }} s</span>
        } @else {
          <span i18n="@@shared.retry-after.interval-unavailable">
            The server did not say how long to wait
          </span>
        }
        @if (retryEligible()) {
          <span i18n="@@shared.retry-after.eligible">You can retry now.</span>
        } @else {
          <span i18n="@@shared.retry-after.waiting">Retry available after the wait</span>
        }
      </p>
      <dl class="m-0 grid gap-2 text-sm [&_dd]:m-0 [&_dt]:text-muted-foreground">
        @if (code(); as errorCode) {
          <div>
            <dt i18n="@@shared.retry-after.error-code">Error code</dt>
            <dd>
              <code>{{ errorCode }}</code>
            </dd>
          </div>
        }
        @if (requestId(); as id) {
          <div>
            <dt i18n="@@shared.retry-after.request-id">Request id</dt>
            <dd>
              <code>{{ id }}</code>
            </dd>
          </div>
        }
      </dl>
      <button
        hlmBtn
        [variant]="'outline'"
        class="justify-self-start"
        type="button"
        [disabled]="!retryEligible() || busy()"
        [attr.aria-busy]="busy()"
        (click)="requestRetry()"
      >
        @if (busy()) {
          <span i18n="@@shared.retry-after.retrying">Retrying…</span>
        } @else if (!retryEligible()) {
          <span i18n="@@shared.retry-after.waiting">Retry available after the wait</span>
        } @else {
          <span i18n="@@shared.retry-after.retry">Retry</span>
        }
      </button>
    </section>
  `,
})
export class RetryAfterComponent {
  readonly kind = input<'rate_limited' | 'unavailable'>('rate_limited');
  readonly retryAfterSeconds = input<number | null>(null);
  readonly retryEligible = input(false);
  readonly busy = input(false);
  readonly code = input<string | null>(null);
  readonly requestId = input<string | null>(null);
  readonly retry = output<void>();

  // This is the supplied interval, not remaining time. The owner controls eligibility.
  protected readonly interval = computed(() => {
    const seconds = this.retryAfterSeconds();
    return seconds !== null && Number.isFinite(seconds) && seconds >= 0 ? seconds : null;
  });

  protected requestRetry(): void {
    if (this.retryEligible() && !this.busy()) this.retry.emit();
  }
}
