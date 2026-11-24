import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { EvidenceRevision, RunStateDto } from '@evidgate/sdk';
import { HlmAlert } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { RunStatePillComponent } from '../../../shared/ui/chips/run-state-pill.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { CopyValueButtonComponent } from '../../../shared/ui/values/copy-value-button.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { EvidenceBadgeComponent } from '../../../shared/ui/verdict/evidence-badge.component';
import { DecisionChipComponent } from '../../../shared/ui/verdict/decision-chip.component';
import { OutcomePillComponent } from '../../../shared/ui/verdict/outcome-pill.component';
import type { Completeness } from '../../../shared/ui/verdict/verdict-values';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { RUN_DETAIL_NOT_PROVIDED, runValueLabel } from './run-value-label';
import type { DecisionViewModel } from './decision/decision-view-model';

@Component({
  selector: 'app-run-header',
  imports: [
    AbsoluteTimeComponent,
    RunStatePillComponent,
    StatusPillComponent,
    EvidenceBadgeComponent,
    DecisionChipComponent,
    OutcomePillComponent,
    CopyableIdentifierComponent,
    HlmIcon,
    HlmAlert,
    HlmButton,
    HlmCard,
    HlmCardTitle,
    CopyValueButtonComponent,
    NumberValueComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-header.component.html',
  host: {
    class: 'block min-w-0 text-foreground wrap-anywhere',
  },
})
export class RunHeaderComponent {
  readonly run = input.required<RunStateDto>();
  protected readonly sectionSize = 'section' as const;
  readonly view = input<'summary' | 'context'>('summary');
  readonly viewEvidence = output<void>();
  // The incomplete-collection warning still describes the latest freeze, regardless of the pin.
  readonly runCompleteness = input<Completeness | null>(null);
  readonly selectedEvidence = input<{
    readonly completeness: Completeness;
    readonly missing: readonly string[];
  } | null>(null);
  readonly evidenceQualifier = input<string | null>(null);
  readonly latestSnapshot = input<EvidenceRevision | null>(null);
  readonly decision = input<DecisionViewModel | null>(null);
  readonly decisionLoading = input(false);
  protected readonly display = escapeFormatChars;
  protected readonly valueLabel = runValueLabel;
  protected readonly copy = {
    id: $localize`:@@runs.detail.id:Run id`,
    state: $localize`:@@runs.detail.state:Collection state`,
    version: $localize`:@@runs.detail.run-version:Run version`,
    purpose: $localize`:@@runs.detail.purpose:Purpose`,
    notProvided: RUN_DETAIL_NOT_PROVIDED,
    missing: $localize`:@@runs.evidence.missing-title:Missing evidence reasons`,
    latestFreeze: $localize`:@@runs.detail.latest-freeze:Latest loaded freeze`,
    ref: $localize`:@@runs.detail.ref:Ref`,
    refKind: $localize`:@@runs.detail.ref-kind:Ref kind`,
    pr: $localize`:@@runs.detail.pr:Pull request number`,
    head: $localize`:@@runs.detail.head:Head commit`,
    base: $localize`:@@runs.detail.base:Base commit`,
    trigger: $localize`:@@runs.detail.trigger:Trigger event`,
    fork: $localize`:@@runs.detail.fork:Fork`,
    yes: $localize`:@@runs.detail.yes:Yes`,
    no: $localize`:@@runs.detail.no:No`,
    unverified: $localize`:@@runs.detail.unverified:Unverified ref`,
    incomplete: $localize`:@@runs.detail.incomplete-summary:Collection is incomplete. Review the evidence panel for recorded missing reasons.`,
    viewEvidence: $localize`:@@runs.detail.view-evidence:View evidence`,
    evidence: $localize`:@@runs.detail.evidence:Evidence revision`,
    completeness: $localize`:@@runs.detail.evidence-completeness:Evidence completeness`,
    outcome: $localize`:@@runs.detail.test-outcome:Test outcome`,
    decision: $localize`:@@runs.detail.stored-decision:Stored run decision`,
    unavailable: $localize`:@@runs.value.decision-unavailable:Unavailable`,
    loading: $localize`:@@runs.decision.loading:Loading live decision`,
    none: $localize`:@@runs.detail.none:None recorded`,
    context: $localize`:@@runs.detail.context:Execution context`,
    environment: $localize`:@@runs.detail.environment:Environment`,
    revision: $localize`:@@runs.detail.release.environment:Environment revision`,
  };
}
