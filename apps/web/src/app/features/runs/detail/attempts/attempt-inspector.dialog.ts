import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmDialogHeader, HlmDialogService, HlmDialogTitle } from '@evidgate/ui/dialog';
import { injectBrnDialogContext } from '@spartan-ng/brain/dialog';
import { CopyableIdentifierComponent } from '../../../../shared/ui/values/copyable-identifier.component';
import { escapeFormatChars } from '../../../../shared/ui/values/ref-name-display';
import { runValueLabel } from '../run-value-label';
import { preHealingFailureFor } from './attempt-groups';
import type { AttemptRow } from './attempt-summary';

interface AttemptInspectorInput {
  readonly attempt: AttemptRow;
  readonly attempts: readonly AttemptRow[];
}

const TITLE = $localize`:@@runs.detail.attempts.inspector:Attempt details`;

export function openAttemptInspector(
  dialogs: HlmDialogService,
  input: AttemptInspectorInput,
): void {
  dialogs.open(AttemptInspectorDialog, {
    ariaLabel: TITLE,
    size: 'lg',
    restoreFocus: true,
    context: input,
  });
}

@Component({
  selector: 'app-attempt-inspector',
  imports: [HlmDialogHeader, HlmDialogTitle, CopyableIdentifierComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 wrap-anywhere' },
  templateUrl: './attempt-inspector.dialog.html',
})
class AttemptInspectorDialog {
  private readonly context = injectBrnDialogContext<AttemptInspectorInput>();
  protected readonly attempt = this.context.attempt;
  // The endpoint pages attempts, so these are only the retries known on the current page.
  protected readonly chain = this.context.attempts
    .filter((row) => row.executionKey === this.attempt.executionKey)
    .sort((a, b) => a.attemptNo - b.attemptNo);
  protected readonly failure = preHealingFailureFor(this.chain);
  protected readonly display = escapeFormatChars;
  protected readonly statusLabel = runValueLabel;
  protected readonly copy = {
    title: TITLE,
    execution: $localize`:@@runs.detail.attempts.execution:Execution`,
    attempt: $localize`:@@runs.detail.attempts.attempt:Attempt`,
    raw: $localize`:@@runs.detail.attempts.raw:Raw status`,
    normalised: $localize`:@@runs.detail.attempts.normalised:Normalised status`,
    chain: $localize`:@@runs.detail.attempts.retry-chain:Retry chain on this page`,
    preHealing: $localize`:@@runs.detail.attempts.pre-healing:Pre-healing failure`,
    candidate: $localize`:@@runs.detail.attempts.candidate:Healing candidate`,
    shadow: $localize`:@@runs.detail.attempts.shadow:Shadow evaluation`,
    official: $localize`:@@runs.detail.attempts.official:Official evaluation`,
  };
}
