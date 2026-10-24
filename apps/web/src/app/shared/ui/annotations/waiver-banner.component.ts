import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

export type WaiverState = 'active' | 'expired' | 'revoked';

@Component({
  selector: 'app-waiver-banner',
  standalone: true,
  imports: [DatePipe],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './waiver-banner.component.css',
  template: `
    <section
      class="waiver"
      [class.waiver-expired]="state() === 'expired'"
      [class.waiver-revoked]="state() === 'revoked'"
      aria-label="Waiver details"
      i18n-aria-label="@@shared.waiver-banner.label"
    >
      <h3>
        @if (!approver().trim() || !utcExpiry()) {
          <span i18n="@@shared.waiver-banner.details-unavailable">Waiver details unavailable</span>
        } @else {
          @switch (state()) {
            @case ('active') {
              <span i18n="@@shared.waiver-banner.active">Approved by waiver</span>
            }
            @case ('expired') {
              <span i18n="@@shared.waiver-banner.expired">Waiver expired</span>
            }
            @case ('revoked') {
              <span i18n="@@shared.waiver-banner.revoked">Waiver revoked</span>
            }
          }
        }
      </h3>
      <dl>
        <div>
          <dt i18n="@@shared.waiver-banner.approver">Approved by</dt>
          <dd class="waiver-approver">
            @if (approver().trim()) {
              {{ approver() }}
            } @else {
              <span i18n="@@shared.waiver-banner.approver-unavailable">Approver unavailable</span>
            }
          </dd>
        </div>
        <div>
          <dt i18n="@@shared.waiver-banner.expires">Expires</dt>
          <dd>
            @if (utcExpiry(); as expiry) {
              <time [attr.datetime]="expiry">{{ expiry | date: 'medium' : '+0000' }}</time>
            } @else {
              <span i18n="@@shared.waiver-banner.expiry-unavailable">Expiry unavailable</span>
            }
          </dd>
        </div>
        @if (utcExpiry(); as expiry) {
          <div>
            <dt i18n="@@shared.waiver-banner.utc-timestamp">UTC timestamp</dt>
            <dd>
              <code class="waiver-utc">{{ expiry }}</code>
            </dd>
          </div>
        }
      </dl>
      <p i18n="@@shared.waiver-banner.unchanged">
        This waiver does not change the stored outcome or decision.
      </p>
    </section>
  `,
})
export class WaiverBanner {
  readonly approver = input.required<string>();
  readonly expiresAt = input.required<string | Date>();
  readonly state = input<WaiverState>('active');

  // Render metadata only: never infer active/expired state from the browser clock.
  protected readonly utcExpiry = computed(() => {
    const supplied = this.expiresAt();
    // Offset-free ISO timestamps use UTC, not the viewer's device timezone.
    const absolute =
      typeof supplied === 'string' &&
      /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2}(?:\.\d+)?)?$/.test(supplied)
        ? `${supplied}Z`
        : supplied;
    const date = absolute instanceof Date ? absolute : new Date(absolute);
    return Number.isNaN(date.getTime()) ? null : date.toISOString();
  });
}
