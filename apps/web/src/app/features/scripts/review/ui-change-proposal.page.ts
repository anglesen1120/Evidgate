import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmTableImports } from '@evidgate/ui/table';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { BADGE_CLASSES, BADGE_TONE_CLASSES } from '../../../shared/ui/chips/badge-classes';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { PageHeaderComponent, HeaderActionsDirective } from '../../../shared/ui/page-header';
import { EmptyPatternComponent } from '../../../shared/ui/states/empty-pattern.component';
import { LoadingPatternComponent } from '../../../shared/ui/states/loading-pattern.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import { EvidenceBadgeComponent } from '../../../shared/ui/verdict/evidence-badge.component';
import { OutcomePillComponent } from '../../../shared/ui/verdict/outcome-pill.component';
import {
  uiChangeProposalFixture,
  type UiChangeProposalViewModel,
} from './ui-change-proposal.fixture';
import { proposalCopy } from './ui-change-proposal.copy';
import { proposalNotices, type UiChangeProposalState } from './ui-change-proposal.states';
import { injectReviewReturnUrl, isNoticeKey, matchesReviewRecord } from './scripts-review-state';

// Review-only until the proposal workflow lands. The real data service will replace the fixture.
@Component({
  selector: 'app-ui-change-proposal-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    RouterLink,
    HlmBadge,
    HlmButton,
    HlmCardImports,
    HlmDialogImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    HlmTableImports,
    AbsoluteTimeComponent,
    StatusPillComponent,
    PageHeaderComponent,
    HeaderActionsDirective,
    EmptyPatternComponent,
    LoadingPatternComponent,
    InlineFeedbackComponent,
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
  ],
  host: { class: 'grid min-w-0 gap-4' },
  templateUrl: './ui-change-proposal.page.html',
})
export class UiChangeProposalPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly policy = inject(NarrowMutationPolicy);
  protected readonly returnUrl = injectReviewReturnUrl();
  readonly viewModel = input<UiChangeProposalViewModel>(uiChangeProposalFixture);
  protected readonly c = proposalCopy;
  private readonly params = toSignal(this.route.queryParamMap);
  protected readonly state = linkedSignal<UiChangeProposalState>(() => {
    const params = this.params();
    const key = params?.get('state');
    const model = this.viewModel();
    if (
      !matchesReviewRecord(params, {
        project: ['p-storefront'],
        proposal: [model.id],
        incident: [model.incident],
        case: [model.manualCase, 'case-total-2'],
        revision: [model.approvedVersion],
        run: [model.run],
        evidence: [model.revision],
      })
    )
      return 'unavailable';
    if (!key || key === 'default' || key === 'ready') return this.viewModel().status;
    if (key === 'success') return 'queued';
    if (key === '403') return 'denied';
    if (key === '412') return 'stale';
    return isNoticeKey(key, proposalNotices) ? key : '404';
  });
  protected readonly notice = computed(() => {
    const k = this.state();
    return k === 'ready' ? null : proposalNotices[k];
  });
  protected readonly canDecide = computed(
    () => this.policy.canMutate() && ['ready', 'long', 'reject-error'].includes(this.state()),
  );
  protected readonly reason = signal('');
  protected readonly invalid = signal(false);
  protected readonly decision = computed(() => {
    switch (this.state()) {
      case 'queued':
      case 'duplicate':
        return this.c.queued;
      case 'rejected':
        return this.c.rejected;
      case 'stale':
        return this.c.stale;
      case 'queueing':
        return this.c.queueing;
      case 'rejecting':
        return this.c.rejecting;
      default:
        return this.c.awaiting;
    }
  });
  protected readonly pendingClasses = BADGE_CLASSES + ' ' + BADGE_TONE_CLASSES.pending;
  protected restore(): void {
    this.state.set('ready');
  }
  protected previewDecision(reject: boolean): boolean {
    if (!this.canDecide() || !this.policy.allowsMutation()) return false;
    if (reject && !this.reason().trim()) {
      this.invalid.set(true);
      return false;
    }
    this.state.set(
      reject ? (this.params()?.get('response') === 'fail' ? 'reject-error' : 'rejected') : 'queued',
    );
    return true;
  }
}
