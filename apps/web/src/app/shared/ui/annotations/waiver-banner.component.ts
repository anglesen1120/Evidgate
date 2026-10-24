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
          @default {
            <span i18n="@@shared.waiver-banner.state-unavailable">Waiver state unavailable</span>
          }
        }
      </h3>
      @if (!approver().trim() || !utcExpiry()) {
        <p i18n="@@shared.waiver-banner.details-unavailable">Waiver details unavailable</p>
      }
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
    if (supplied instanceof Date) {
      return Number.isNaN(supplied.getTime()) ? null : supplied.toISOString();
    }

    // Accept date-only UTC or ISO timestamps with an explicit zone, never device-local time.
    const parts =
      /^(\d{4})-(\d{2})-(\d{2})(?:T(\d{2}):(\d{2})(?::(\d{2})(\.\d+)?)?(Z|[+-]\d{2}:\d{2}))?$/.exec(
        supplied,
      );
    if (!parts) return null;
    const [
      ,
      year,
      month,
      day,
      hour = '00',
      minute = '00',
      second = '00',
      fraction = '',
      zone = 'Z',
    ] = parts;
    if (Number(hour) > 23 || Number(minute) > 59 || Number(second) > 59) return null;

    // Check the supplied calendar day before applying its offset, which can cross UTC midnight.
    const date = new Date(`${year}-${month}-${day}T${hour}:${minute}:${second}${fraction}Z`);
    if (
      date.getUTCFullYear() !== Number(year) ||
      date.getUTCMonth() + 1 !== Number(month) ||
      date.getUTCDate() !== Number(day)
    ) {
      return null;
    }

    if (zone !== 'Z') {
      const offsetHours = Number(zone.slice(1, 3));
      const offsetMinutes = Number(zone.slice(4, 6));
      if (offsetHours > 23 || offsetMinutes > 59) return null;
      const offset = (offsetHours * 60 + offsetMinutes) * (zone[0] === '+' ? 1 : -1);
      date.setUTCMinutes(date.getUTCMinutes() - offset);
    }
    return date.toISOString();
  });
}
