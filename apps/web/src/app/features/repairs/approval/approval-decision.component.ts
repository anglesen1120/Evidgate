import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { InlineFeedbackComponent } from '../../../shared/ui/states';
import type { RepairReviewContext } from '../shadow/repair-review-context';
import { REPAIR } from '../shadow/repair-review.fixture';
import { SHADOW_COPY } from '../shadow/shadow.copy';
import { ProposalStatusComponent } from '../shadow/proposal-status.component';
import { APPROVAL_COPY } from './approval.copy';
import { ApprovalPreviewStore } from './approval-preview.store';

@Component({
  selector: 'app-approval-decision',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmCardImports,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    InlineFeedbackComponent,
    ProposalStatusComponent,
  ],
  providers: [ApprovalPreviewStore],
  templateUrl: './approval-decision.component.html',
  styleUrl: '../shadow/repair-panels.css',
})
export class ApprovalDecisionComponent {
  protected readonly store = inject(ApprovalPreviewStore);
  protected readonly viewport = inject(NarrowMutationPolicy);
  readonly context = input.required<RepairReviewContext>();
  protected readonly copy = APPROVAL_COPY;
  protected readonly labels = SHADOW_COPY;
  protected readonly record = REPAIR;
  protected readonly eligible = computed(
    () =>
      ['default', '422', 'prepared'].includes(this.context().state()) && this.viewport.canMutate(),
  );
  protected readonly blocker = computed(() => {
    const state = this.context().state();
    switch (state) {
      case 'featureoff':
      case 'disabled':
      case 'shadowonly':
      case 'conflict':
      case 'submitting':
        return this.copy[state];
      case 'unavailable':
      case 'missing':
      case 'negative':
      case 'running':
      case 'proposed':
      case 'expired':
      case 'stale':
        return this.labels[state];
      default:
        return this.viewport.canMutate() ? this.copy.blocked : this.copy.narrow;
    }
  });
  protected open(action: 'approve' | 'reject'): void {
    if (this.eligible() && this.viewport.allowsMutation()) this.store.open(action);
  }
  protected prepare(): void {
    if (this.eligible() && this.viewport.allowsMutation()) this.store.prepare();
  }
}
