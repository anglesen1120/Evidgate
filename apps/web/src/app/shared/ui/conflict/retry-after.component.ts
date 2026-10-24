import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';

@Component({
  selector: 'app-retry-after',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './retry-after.component.css',
  template: `
    <section class="retry-panel">
      @if (kind() === 'unavailable') {
        <h2 i18n="@@shared.retry-after.unavailable-title">503 · Service unavailable</h2>
      } @else {
        <h2 i18n="@@shared.retry-after.title">429 · Rate limited</h2>
      }
      <p role="status" [attr.aria-live]="'polite'" aria-atomic="true">
        @if (interval(); as seconds) {
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
      <dl>
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
      <button type="button" [disabled]="!retryEligible() || busy()" (click)="requestRetry()">
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
    return seconds !== null && Number.isFinite(seconds) && seconds > 0 ? seconds : null;
  });

  protected requestRetry(): void {
    if (this.retryEligible() && !this.busy()) this.retry.emit();
  }
}
