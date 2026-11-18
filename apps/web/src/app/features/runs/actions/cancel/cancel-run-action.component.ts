import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { InlineFeedbackComponent } from '../../../../shared/ui/states/inline-feedback.component';
import { RequestStateComponent } from '../../../../shared/ui/states/request-state.component';
import { CancelRunDialogComponent } from './cancel-run-dialog.component';
import { isTerminalRun } from './cancel-run-state';
import { CANCEL_RUN_COPY } from './cancel-run.copy';
import { CancelRunStore } from './cancel-run.store';

@Component({
  selector: 'app-cancel-run-action',
  imports: [HlmButton, InlineFeedbackComponent, RequestStateComponent, CancelRunDialogComponent],
  providers: [CancelRunStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 max-w-prose justify-items-start gap-(--eg-space-3)' },
  template: `
    <button
      hlmBtn
      type="button"
      [variant]="'outline'"
      data-run-cancel-trigger
      class="min-h-(--eg-control-touch)"
      [attr.aria-disabled]="!store.canCancel() ? 'true' : null"
      [tabIndex]="store.canCancel() ? 0 : -1"
      [attr.aria-describedby]="
        disabledReason()
          ? 'run-cancel-disabled-reason'
          : !store.opened() && announcement()
            ? 'run-cancel-status'
            : null
      "
      (click)="store.open()"
    >
      {{ copy.action }}
    </button>
    @if (disabledReason(); as reason) {
      <p id="run-cancel-disabled-reason" class="m-0 text-muted-foreground">{{ reason }}</p>
    }
    @if (!store.opened() && announcement(); as announcement) {
      <app-inline-feedback
        id="run-cancel-status"
        [title]="announcement"
        data-run-cancel-status
        [actionLabel]="store.phase() === 'waiting' ? copy.check : undefined"
        (action)="store.checkState()"
      />
      @if (store.phase() === 'poll-error') {
        <app-request-state
          [status]="'error'"
          [error]="store.error()"
          [retryEligible]="store.retryReady()"
          (retry)="store.checkState()"
        />
      }
    }
    @if (store.opened()) {
      <app-cancel-run-dialog />
    }
  `,
})
export class CancelRunActionComponent {
  protected readonly store = inject(CancelRunStore);
  protected readonly copy = CANCEL_RUN_COPY;
  protected readonly announcement = computed(() => {
    switch (this.store.phase()) {
      case 'polling':
        return this.copy.recorded;
      case 'canceled':
        return this.copy.canceled;
      case 'terminal':
        return this.copy.completed;
      case 'waiting':
        return this.copy.waiting;
      case 'poll-error':
        return this.copy.pollError;
      default:
        return '';
    }
  });
  protected readonly disabledReason = computed(() => {
    if (this.store.scope() === null) return this.copy.noPermission;
    const state = this.store.detail.run()?.state;
    if (state && isTerminalRun(state)) return this.copy.terminal;
    if (!this.store.viewport.canMutate()) return this.copy.desktop;
    return this.store.phase() === 'submitting' ? this.copy.submitting : '';
  });
}
