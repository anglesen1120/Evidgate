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
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { AuditStore } from './audit.store';

// Let navigation consume the object URL before releasing it on the next task.
function revokeDeferred(url: string): void {
  setTimeout(() => URL.revokeObjectURL(url), 0);
}

@Component({
  selector: 'app-audit-export',
  imports: [HlmAlertImports, HlmButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class: 'block min-w-0 max-w-full',
    '[attr.data-audit-export-phase]': 'store.exportState().phase',
  },
  template: `
    <div class="grid min-w-0 gap-3 wrap-anywhere [&_p]:m-0" [attr.aria-busy]="running()">
      <button
        type="button"
        hlmBtn
        class="min-h-[var(--eg-control-touch)] whitespace-normal"
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
              <p
                hlmAlert
                class="bg-muted text-muted-foreground"
                role="status"
                data-audit-export-capped
              >
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
          <div
            hlmAlert
            [variant]="'destructive'"
            class="grid gap-3"
            role="alert"
            data-audit-export-error
          >
            @if (state.error; as failure) {
              <p>{{ failure.message }}</p>
              <code>{{ failure.code }}</code>
            }
            <button
              hlmBtn
              [variant]="'outline'"
              class="min-h-[var(--eg-control-touch)] whitespace-normal"
              type="button"
              (click)="download()"
            >
              {{ copy.retry }}
            </button>
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
  // The safety page bound can stop below the row cap; report the actual exported count.
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
