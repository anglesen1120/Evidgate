import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { RunStateDto } from '@evidgate/sdk';
import { HlmAlert } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { RunStatePillComponent } from '../../../shared/ui/chips/run-state-pill.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { CopyValueButtonComponent } from '../../../shared/ui/values/copy-value-button.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { EvidenceBadgeComponent } from '../../../shared/ui/verdict/evidence-badge.component';
import type { Completeness } from '../../../shared/ui/verdict/verdict-values';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { RUN_DETAIL_NOT_PROVIDED, runValueLabel } from './run-value-label';

@Component({
  selector: 'app-run-header',
  imports: [
    RunStatePillComponent,
    StatusPillComponent,
    EvidenceBadgeComponent,
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
    class:
      'block min-w-0 text-foreground wrap-anywhere [&_dl]:m-0 [&_dl]:grid [&_dl]:gap-(--eg-space-5) [&_dl]:grid-cols-1 min-[768px]:[&_dl]:grid-cols-2 [&_dl>div]:min-w-0 [&_dt]:mb-(--eg-space-2) [&_dt]:text-muted-foreground [&_dd]:m-0 [&_dd]:min-w-0 [&_dd]:grid [&_dd]:justify-items-start [&_dd]:gap-(--eg-space-2) [&_bdi]:max-w-full',
  },
})
export class RunHeaderComponent {
  readonly run = input.required<RunStateDto>();
  protected readonly sectionSize = 'section' as const;
  readonly view = input<'summary' | 'context'>('summary');
  readonly viewEvidence = output<void>();
  /**
   * Collection completeness of the newest frozen revision, when the revision list answered. The
   * header badge describes the run's collection, never the revision the evidence region is pinned
   * to.
   */
  readonly runCompleteness = input<Completeness | null>(null);
  protected readonly display = escapeFormatChars;
  protected readonly valueLabel = runValueLabel;
  protected readonly copy = {
    id: $localize`:@@runs.detail.id:Run id`,
    state: $localize`:@@runs.detail.state:Collection state`,
    version: $localize`:@@runs.detail.version:Version`,
    purpose: $localize`:@@runs.detail.purpose:Purpose`,
    notProvided: RUN_DETAIL_NOT_PROVIDED,
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
    none: $localize`:@@runs.detail.none:None recorded`,
    context: $localize`:@@runs.detail.context:Execution context`,
    environment: $localize`:@@runs.detail.environment:Environment`,
    revision: $localize`:@@runs.detail.environment-revision:Revision`,
  };
}
