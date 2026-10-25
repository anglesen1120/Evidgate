import { formatNumber } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  LOCALE_ID,
  computed,
  inject,
  input,
} from '@angular/core';
import { AuditStore } from './audit.store';

/**
 * P3 review: `URL.revokeObjectURL()` in the same task as `anchor.click()` can cancel the download
 * in engines that start the navigation asynchronously, so the revoke is deferred by one task. The
 * anchor is still detached synchronously in `finally`, so a throwing `click()` leaves no residue,
 * and a component destroyed before the timer fires still revokes its own URL instead of leaking it.
 */
function revokeDeferred(url: string): void {
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

@Component({
  selector: 'app-audit-export',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './audit.page.css',
  host: { '[attr.data-audit-export-phase]': 'store.exportState().phase' },
  template: `
    <div class="export-content">
      <button
        type="button"
        class="primary"
        data-audit-export
        [disabled]="running() || !orgId()"
        (click)="download()"
      >
        {{ copy.export }}
      </button>
      <div [attr.aria-live]="'polite'" aria-atomic="true">
        @if (store.exportState(); as state) {
          @if (state.phase === 'running') {
            <p role="status">{{ progress(state.rows) }}</p>
          }
          @if (state.phase === 'done') {
            @if (state.capped) {
              <p class="banner" role="status" data-audit-export-capped>
                {{ cappedMessage(state.rows) }}
              </p>
            } @else {
              <p role="status" data-audit-export-done>{{ completed(state.rows) }}</p>
            }
          }
        }
      </div>
      @if (store.exportState(); as state) {
        @if (state.phase === 'failed') {
          <div class="banner failure" role="alert" data-audit-export-error>
            @if (state.error; as failure) {
              <p>{{ failure.message }}</p>
              <code>{{ failure.code }}</code>
            }
            <button type="button" (click)="download()">{{ copy.retry }}</button>
          </div>
        }
      }
    </div>
  `,
})
export class AuditExportComponent {
  readonly orgId = input.required<string>();
  protected readonly store = inject(AuditStore);
  private readonly lifetime = inject(DestroyRef);
  /** The active build/app locale: the banner's count is a number, so the locale groups it. */
  private readonly locale = inject(LOCALE_ID);
  protected readonly running = computed(() => this.store.exportState().phase === 'running');
  protected readonly copy = {
    export: $localize`:@@settings.audit.export:Export CSV`,
    retry: $localize`:@@settings.audit.retry:Retry`,
  };
  // A5 review: the number is formatted through the active locale (`10,000` en, `10.000` vi)
  // instead of `toLocaleString('en-US')`, which printed `10,000` in every locale's banner.
  // F-4: the banner prints the rows actually exported, not `AUDIT_EXPORT_CAP` — `capped` is also
  // true when the export stops at its safety page bound with a cursor still named, and then only a
  // few hundred rows were collected. At the row cap the two are the same number, so the en copy of
  // AC7 is unchanged. The placeholder id is `rows` in both catalogs.
  protected cappedMessage(rows: number): string {
    return $localize`:@@settings.audit.export-capped:First ${formatNumber(rows, this.locale)}:rows: rows exported — narrow the filter for a complete export.`;
  }
  protected progress(rows: number): string {
    return $localize`:@@settings.audit.export-progress:Exporting… ${rows}:rows: rows`;
  }
  protected completed(rows: number): string {
    return $localize`:@@settings.audit.export-done:Exported ${rows}:rows: rows.`;
  }

  protected async download(): Promise<void> {
    if (this.running() || !this.orgId()) return;
    const orgId = this.orgId();
    const csv = await this.store.exportCsv(orgId);
    if (csv === null || this.lifetime.destroyed || orgId !== this.orgId()) return;
    const url = URL.createObjectURL(new Blob([csv], { type: 'text/csv;charset=utf-8' }));
    const anchor = document.createElement('a');
    try {
      anchor.href = url;
      anchor.download = `evidgate-audit-${orgId}-${new Date().toISOString().slice(0, 10)}.csv`;
      document.body.append(anchor);
      anchor.click();
    } finally {
      anchor.remove();
      revokeDeferred(url);
    }
  }
}
