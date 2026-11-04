import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { RunStateDto } from '@evidgate/sdk';
import { RunStatePillComponent } from '../../../shared/ui/chips/run-state-pill.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { EvidenceBadgeComponent } from '../../../shared/ui/verdict/evidence-badge.component';
import type { Completeness } from '../../../shared/ui/verdict/verdict-values';
import { escapeFormatChars } from '../list/ref-name-display';

@Component({
  selector: 'app-run-header',
  imports: [RunStatePillComponent, StatusPillComponent, EvidenceBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-header.component.html',
  styleUrl: './run-detail-metadata.css',
})
export class RunHeaderComponent {
  readonly run = input.required<RunStateDto>();
  readonly view = input<'summary' | 'context'>('summary');
  readonly viewEvidence = output<void>();
  /**
   * Collection completeness of the newest frozen revision, when the revision list answered. The
   * header badge describes the run's collection, never the revision the evidence region is pinned
   * to.
   */
  readonly runCompleteness = input<Completeness | null>(null);
  protected readonly display = escapeFormatChars;
  protected readonly copy = {
    id: $localize`:@@runs.detail.id:Run id`,
    state: $localize`:@@runs.detail.state:Collection state`,
    version: $localize`:@@runs.detail.version:Version`,
    purpose: $localize`:@@runs.detail.purpose:Purpose`,
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
