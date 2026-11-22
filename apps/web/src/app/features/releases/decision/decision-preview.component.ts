import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { decisionCopy as copy } from './decision-review.copy';
import { runFacts, type PreviewAction } from './decision-review.fixture';

const help: Record<PreviewAction, string> = {
  request: copy.requestHelp,
  approve: copy.approveHelp,
  decline: copy.declineHelp,
  revoke: copy.revokeHelp,
  remove: copy.removeHelp,
  add: copy.addContext,
};
const PreviewStatus = signalStore(
  withState({ finished: false }),
  withMethods((store) => ({
    finish(finished: boolean) {
      patchState(store, { finished });
    },
  })),
);

@Component({
  selector: 'app-decision-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmDialogImports, HlmInput, ShellIconComponent],
  providers: [PreviewStatus],
  templateUrl: './decision-preview.component.html',
  host: { class: 'block min-w-0 max-w-full' },
})
export class DecisionPreview {
  protected readonly status = inject(PreviewStatus);
  readonly action = input.required<PreviewAction>();
  readonly disabledReason = input<string | null>(null);
  protected readonly copy = copy;
  protected readonly facts = runFacts;
  protected readonly title = computed(() => copy[this.action()]);
  protected readonly description = computed(() => help[this.action()]);
  protected readonly destructive = computed(
    () => this.action() === 'decline' || this.action() === 'revoke' || this.action() === 'remove',
  );
  protected readonly withExpiry = computed(
    () => this.action() === 'add' || this.action() === 'request',
  );
  protected readonly withReason = computed(
    () => this.withExpiry() || this.action() === 'revoke' || this.action() === 'decline',
  );

  protected finish(event: Event, form: HTMLFormElement, dialog: HlmDialog): void {
    event.preventDefault();
    if (this.disabledReason()) return;
    const reason = form.elements.namedItem('reason');
    if (reason instanceof HTMLTextAreaElement && !reason.value.trim())
      reason.setCustomValidity(copy.reasonRequired);
    if (!form.reportValidity()) return;
    this.status.finish(true);
    dialog.close();
  }
}
