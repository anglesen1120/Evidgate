import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { RunStateDto } from '@evidgate/sdk';
import { RunStatePillComponent } from '../../../shared/ui/chips/run-state-pill.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { EvidenceBadgeComponent } from '../../../shared/ui/verdict/evidence-badge.component';
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
    evidence: $localize`:@@runs.detail.evidence:Evidence revision`,
    none: $localize`:@@runs.detail.none:None recorded`,
    context: $localize`:@@runs.detail.context:Execution context`,
    environment: $localize`:@@runs.detail.environment:Environment`,
  };
}
