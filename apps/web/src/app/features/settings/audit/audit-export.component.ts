import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  inject,
  input,
} from '@angular/core';
import { AUDIT_EXPORT_CAP, AuditStore } from './audit.store';

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
              <p class="banner" role="status" data-audit-export-capped>{{ cappedMessage }}</p>
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
  protected readonly running = computed(() => this.store.exportState().phase === 'running');
  protected readonly copy = {
    export: $localize`:@@settings.audit.export:Export CSV`,
    retry: $localize`:@@settings.audit.retry:Retry`,
  };
  protected readonly cappedMessage = $localize`:@@settings.audit.export-capped:First ${AUDIT_EXPORT_CAP.toLocaleString('en-US')}:cap: rows exported — narrow the filter for a complete export.`;
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
      URL.revokeObjectURL(url);
    }
  }
}
