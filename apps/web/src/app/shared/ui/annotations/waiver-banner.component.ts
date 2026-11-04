import { DatePipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmAlert, HlmAlertDescription, HlmAlertTitle } from '@evidgate/ui/alert';

export type WaiverState = 'active' | 'expired' | 'revoked';

@Component({
  selector: 'app-waiver-banner',
  imports: [DatePipe, HlmAlert, HlmAlertDescription, HlmAlertTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 font-sans text-sm' },
  template: `
    <section
      hlmAlert
      role="region"
      class="waiver min-w-0 gap-(--eg-space-3) border-s-(length:--eg-space-2) whitespace-normal wrap-anywhere"
      [class]="tone()"
      [class.waiver-expired]="state() === 'expired'"
      [class.waiver-revoked]="state() === 'revoked'"
      aria-label="Waiver details"
      i18n-aria-label="@@shared.waiver-banner.label"
    >
      <h3 hlmAlertTitle class="m-0 text-sm font-semibold">
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
        <p class="m-0" i18n="@@shared.waiver-banner.details-unavailable">
          Waiver details unavailable
        </p>
      }
      <dl class="m-0 flex min-w-0 flex-col gap-(--eg-space-3)">
        <div class="flex min-w-0 flex-wrap gap-(--eg-space-3)">
          <dt class="font-semibold" i18n="@@shared.waiver-banner.approver">Approved by</dt>
          <dd class="waiver-approver m-0 min-w-0 max-w-full">
            @if (approver().trim()) {
              {{ approver() }}
            } @else {
              <span i18n="@@shared.waiver-banner.approver-unavailable">Approver unavailable</span>
            }
          </dd>
        </div>
        <div class="flex min-w-0 flex-wrap gap-(--eg-space-3)">
          <dt class="font-semibold" i18n="@@shared.waiver-banner.expires">Expires</dt>
          <dd class="m-0 min-w-0 max-w-full">
            @if (utcExpiry(); as expiry) {
              <time [attr.datetime]="expiry">{{ expiry | date: 'medium' : '+0000' }}</time>
            } @else {
              <span i18n="@@shared.waiver-banner.expiry-unavailable">Expiry unavailable</span>
            }
          </dd>
        </div>
        @if (utcExpiry(); as expiry) {
          <div class="flex min-w-0 flex-wrap gap-(--eg-space-3)">
            <dt class="font-semibold" i18n="@@shared.waiver-banner.utc-timestamp">UTC timestamp</dt>
            <dd class="m-0 min-w-0 max-w-full">
              <code class="waiver-utc font-mono text-xs">{{ expiry }}</code>
            </dd>
          </div>
        }
      </dl>
      <p hlmAlertDescription class="m-0 text-current" i18n="@@shared.waiver-banner.unchanged">
        This waiver does not change the stored outcome or decision.
      </p>
    </section>
  `,
})
export class WaiverBanner {
  readonly approver = input.required<string>();
  readonly expiresAt = input.required<string | Date>();
  readonly state = input<WaiverState>('active');

  protected readonly tone = computed(() => {
    switch (this.state()) {
      case 'expired':
        return 'bg-(--eg-warning-bg) text-(--eg-warning-fg)';
      case 'revoked':
        return 'bg-(--eg-danger-bg) text-(--eg-danger-fg)';
      default:
        return 'bg-muted text-(--eg-neutral-fg)';
    }
  });

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
