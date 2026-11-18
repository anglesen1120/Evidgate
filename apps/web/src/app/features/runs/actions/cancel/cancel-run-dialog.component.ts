import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { FieldFeedbackComponent } from '../../../../shared/forms/field-feedback.component';
import { FormErrorSummaryComponent } from '../../../../shared/forms/form-error-summary.component';
import {
  createFormFeedback,
  type FieldFeedback,
  type FormFeedback,
} from '../../../../shared/forms/form-feedback';
import { DesktopRequiredNoticeComponent } from '../../../../shared/ui/narrow/desktop-required-notice.component';
import { InlineFeedbackComponent } from '../../../../shared/ui/states/inline-feedback.component';
import { RequestStateComponent } from '../../../../shared/ui/states/request-state.component';
import { CopyableIdentifierComponent } from '../../../../shared/ui/values/copyable-identifier.component';
import { cancelReasonError } from './cancel-run-state';
import { CANCEL_RUN_COPY } from './cancel-run.copy';
import { CancelRunStore } from './cancel-run.store';

@Component({
  selector: 'app-cancel-run-dialog',
  imports: [
    FormsModule,
    HlmButton,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    DesktopRequiredNoticeComponent,
    InlineFeedbackComponent,
    RequestStateComponent,
    CopyableIdentifierComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [role]="'alertdialog'"
      [aria-label]="copy.title"
      [autoFocus]="'[data-run-cancel-keep]'"
      [closeOnOutsidePointerEvents]="false"
      [disableClose]="true"
      [restoreFocus]="false"
      (closed)="finishClose()"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        data-run-cancel-dialog
        class="max-h-[calc(100dvh-var(--eg-space-10))] min-w-0 overflow-y-auto wrap-anywhere"
        (keydown.escape)="escape($event)"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ copy.title }}</h2>
          <p hlmDialogDescription>{{ copy.description }}</p>
        </header>
        @if (!store.viewport.canMutate()) {
          <app-desktop-required-notice id="run-cancel-desktop-required" />
        }
        <app-copyable-identifier [value]="store.origin()?.runId ?? ''" [label]="copy.run" />
        <form
          class="grid min-w-0 gap-(--eg-space-5)"
          [attr.aria-busy]="store.phase() === 'submitting'"
          (ngSubmit)="submit()"
        >
          <app-form-error-summary [summaryId]="'run-cancel-errors'" [feedback]="feedback" />
          <div class="grid min-w-0 gap-(--eg-space-2)">
            <label hlmLabel for="run-cancel-reason">{{ copy.reason }}</label>
            <textarea
              hlmInput
              id="run-cancel-reason"
              name="reason"
              rows="3"
              class="min-h-(--eg-control-touch) resize-y"
              [ngModel]="reason()"
              (ngModelChange)="reason.set($event)"
              [readOnly]="store.hasSubmitted()"
              [aria-describedby]="field.describedBy()"
              [attr.aria-invalid]="field.ariaInvalid()"
              [forceInvalid]="field.invalid()"
              (blur)="field.blur()"
            ></textarea>
            <app-field-feedback [field]="field" />
            @if (store.hasSubmitted()) {
              <p class="m-0 text-muted-foreground">{{ copy.frozen }}</p>
            }
          </div>
          @if (store.phase() === 'polling') {
            <app-inline-feedback [title]="copy.recorded" />
          }
          <app-request-state
            [status]="store.error() ? 'error' : 'ready'"
            [error]="store.error()"
            [retryEligible]="store.retryReady()"
            (retry)="submit()"
          />
          <footer hlmDialogFooter class="gap-(--eg-space-3)">
            <button
              hlmBtn
              type="button"
              [variant]="'outline'"
              data-run-cancel-keep
              class="min-h-(--eg-control-touch)"
              [disabled]="store.phase() === 'submitting'"
              (click)="close()"
            >
              {{ store.hasSubmitted() ? copy.close : copy.keep }}
            </button>
            <button
              hlmBtn
              type="submit"
              [variant]="'destructive'"
              data-run-cancel-confirm
              class="min-h-(--eg-control-touch)"
              [disabled]="!store.canCancel() || store.error() !== null"
              [attr.aria-describedby]="
                !store.viewport.canMutate() ? 'run-cancel-desktop-required' : null
              "
            >
              {{ store.phase() === 'submitting' ? copy.submitting : copy.confirm }}
            </button>
          </footer>
        </form>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class CancelRunDialogComponent {
  protected readonly store = inject(CancelRunStore);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly copy = CANCEL_RUN_COPY;
  protected readonly opened = signal(true);
  protected readonly reason = signal('');
  protected readonly feedback = createFormFeedback([
    {
      id: 'run-cancel-reason',
      label: this.copy.reason,
      hint: this.copy.reasonHint,
      error: () => {
        const error = cancelReasonError(this.reason());
        return error === 'blank'
          ? this.copy.reasonBlank
          : error === 'too-long'
            ? this.copy.reasonLong
            : null;
      },
    },
  ]);
  protected readonly field = firstField(this.feedback);
  private readonly opener =
    this.document.activeElement instanceof HTMLElement ? this.document.activeElement : null;

  constructor() {
    this.destroyRef.onDestroy(() =>
      queueMicrotask(() => {
        if (this.opener?.isConnected) this.opener.focus();
      }),
    );
  }

  protected async submit(): Promise<void> {
    if (!this.opened() || !this.feedback.submit()) return;
    await this.store.confirm(this.reason() === '' ? undefined : this.reason());
    if (!this.destroyRef.destroyed && !['error', 'idle', 'submitting'].includes(this.store.phase()))
      this.close();
  }
  protected close(): void {
    if (this.store.phase() !== 'submitting') this.opened.set(false);
  }
  protected escape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.close();
  }
  protected finishClose(): void {
    this.store.dismiss();
  }
}

/** The reason is this form's only field; a missing entry is a programming error, not a state. */
function firstField(feedback: FormFeedback): FieldFeedback {
  const [field] = feedback.fields;
  if (!field) throw new Error('The cancel form declares one feedback field');
  return field;
}
