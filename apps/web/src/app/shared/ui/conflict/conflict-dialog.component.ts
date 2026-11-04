import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  viewChild,
} from '@angular/core';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';

import type { ApiError } from '../../../core/http/api-error';
import type { ConflictField } from './conflict-details';

@Component({
  selector: 'app-conflict-dialog',
  imports: [HlmAlertImports, HlmBadge, HlmButton, HlmDialogImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog [state]="open() ? 'open' : 'closed'" (stateChanged)="onStateChanged($event)">
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-8))] overflow-y-auto"
      >
        <header hlmDialogHeader class="flex flex-row flex-wrap items-start justify-between gap-2">
          <div class="min-w-0 flex-1">
            <h2 hlmDialogTitle i18n="@@shared.conflict.title">Review conflict</h2>
            <p hlmDialogDescription i18n="@@shared.conflict.description">
              Reload is explicit. Your draft is not merged or resubmitted automatically.
            </p>
          </div>
          <button
            hlmBtn
            [variant]="'ghost'"
            hlmDialogClose
            class="close"
            type="button"
            i18n="@@shared.conflict.close"
          >
            Close
          </button>
        </header>
        <div
          class="body grid gap-4 min-w-0 wrap-anywhere"
          [attr.aria-busy]="reloadState() === 'loading'"
        >
          <dl class="revision m-0 grid gap-1 text-sm [&_dd]:m-0 [&_dt]:text-muted-foreground">
            <dt i18n="@@shared.conflict.current-revision">Current revision</dt>
            <dd>
              @if (currentRevision(); as revision) {
                <code>{{ revision }}</code>
              } @else {
                <span i18n="@@shared.conflict.revision-unavailable">Revision unavailable</span>
              }
            </dd>
          </dl>
          @if (comparisonUnavailable()) {
            <p i18n="@@shared.conflict.comparison-unavailable">
              The current version could not be compared
            </p>
          }
          <div class="fields grid gap-3">
            @for (field of fields(); track field.id) {
              <section class="field rounded-md border border-solid border-border bg-muted p-3">
                <h3 class="m-0 mb-2 flex flex-wrap items-center gap-2 text-base">
                  {{ field.label }}
                  @if (field.local !== null && field.server !== null) {
                    @if (field.local === field.server) {
                      <span
                        hlmBadge
                        [variant]="'outline'"
                        class="marker"
                        i18n="@@shared.conflict.same"
                        >Same</span
                      >
                    } @else {
                      <span
                        hlmBadge
                        [variant]="'secondary'"
                        class="marker"
                        i18n="@@shared.conflict.changed"
                        >Changed</span
                      >
                    }
                  } @else {
                    <span
                      hlmBadge
                      [variant]="'outline'"
                      class="marker"
                      i18n="@@shared.conflict.not-compared"
                      >Not compared</span
                    >
                  }
                </h3>
                <dl
                  class="comparison m-0 grid gap-3 sm:grid-cols-2 [&_dd]:m-0 [&_dd]:whitespace-pre-wrap [&_dt]:mb-1 [&_dt]:text-sm [&_dt]:text-muted-foreground"
                >
                  <div>
                    <dt i18n="@@shared.conflict.local">Your draft</dt>
                    <dd class="local-value">
                      @if (field.local !== null) {
                        {{ field.local }}
                      } @else {
                        <span i18n="@@shared.conflict.local-unavailable"
                          >Draft value unavailable</span
                        >
                      }
                    </dd>
                  </div>
                  <div>
                    <dt i18n="@@shared.conflict.server">Current server value</dt>
                    <dd class="server-value">
                      @if (field.server !== null) {
                        {{ field.server }}
                      } @else {
                        <span i18n="@@shared.conflict.server-unavailable"
                          >Server value unavailable</span
                        >
                      }
                    </dd>
                  </div>
                </dl>
              </section>
            }
          </div>
          @if (reloadError(); as error) {
            <section hlmAlert [variant]="'destructive'" class="error" [attr.aria-live]="'polite'">
              <h3 hlmAlertTitle i18n="@@shared.conflict.reload-error">
                The current version could not be reloaded
              </h3>
              <p hlmAlertDescription>{{ error.message }}</p>
              <dl class="m-0 grid gap-1 [&_dd]:m-0">
                <div>
                  <dt i18n="@@shared.conflict.error-code">Error code</dt>
                  <dd>
                    <code>{{ error.code }}</code>
                  </dd>
                </div>
                @if (error.requestId; as requestId) {
                  <div>
                    <dt i18n="@@shared.conflict.request-id">Request id</dt>
                    <dd>
                      <code>{{ requestId }}</code>
                    </dd>
                  </div>
                }
              </dl>
            </section>
          }
        </div>
        <p
          class="progress m-0 min-h-[var(--eg-leading-sm)] text-sm text-muted-foreground"
          role="status"
          [attr.aria-live]="'polite'"
        >
          @if (reloadState() === 'loading') {
            <span i18n="@@shared.conflict.reloading">Reloading…</span>
          }
        </p>
        <footer hlmDialogFooter>
          <button
            hlmBtn
            [variant]="'outline'"
            hlmDialogClose
            type="button"
            i18n="@@shared.conflict.keep-editing"
          >
            Keep editing
          </button>
          <button
            hlmBtn
            class="reload"
            type="button"
            [disabled]="reloadState() === 'loading'"
            (click)="requestReload()"
            i18n="@@shared.conflict.reload"
          >
            Reload current
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class ConflictDialogComponent {
  // Dismissal emits keepEditing; the owner resets open. A successful reload closes via
  // the input without emitting dismissal. Brain owns focus trapping and restoration.
  readonly open = input(false);
  readonly fields = input<readonly ConflictField[]>([]);
  readonly currentRevision = input<string | null>(null);
  readonly reloadState = input<'idle' | 'loading' | 'error'>('idle');
  readonly reloadError = input<ApiError | null>(null);
  readonly reload = output<void>();
  readonly keepEditing = output<void>();

  protected readonly comparisonUnavailable = computed(
    () => this.fields().length === 0 || this.fields().every((field) => field.server === null),
  );
  private readonly dialog = viewChild(HlmDialog);

  protected requestReload(): void {
    if (this.reloadState() !== 'loading' && this.dialog()?.stateComputed() === 'open') {
      this.reload.emit();
    }
  }

  protected onStateChanged(state: 'open' | 'closed'): void {
    if (state === 'closed' && this.open()) this.keepEditing.emit();
  }
}
