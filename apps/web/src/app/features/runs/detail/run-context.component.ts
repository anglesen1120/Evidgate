import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { RunStateDto } from '@evidgate/sdk';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { CopyValueButtonComponent } from '../../../shared/ui/values/copy-value-button.component';
import { NumberValueComponent } from '../../../shared/ui/values/number-value.component';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { RUN_DETAIL_NOT_PROVIDED, runValueLabel } from './run-value-label';

@Component({
  selector: 'app-run-context',
  imports: [HlmCard, HlmCardTitle, CopyValueButtonComponent, NumberValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './run-context.component.html',
  host: { class: 'block min-w-0 wrap-anywhere' },
})
export class RunContextComponent {
  readonly run = input.required<RunStateDto>();
  protected readonly sectionSize = 'section' as const;
  protected readonly display = escapeFormatChars;
  protected readonly valueLabel = runValueLabel;
  protected readonly copy = {
    version: $localize`:@@runs.detail.run-version:Run version`,
    purpose: $localize`:@@runs.detail.purpose:Purpose`,
    notProvided: RUN_DETAIL_NOT_PROVIDED,
    refKind: $localize`:@@runs.detail.ref-kind:Ref kind`,
    pr: $localize`:@@runs.detail.pr:Pull request number`,
    head: $localize`:@@runs.detail.head:Head commit`,
    base: $localize`:@@runs.detail.base:Base commit`,
    trigger: $localize`:@@runs.detail.trigger:Trigger event`,
    fork: $localize`:@@runs.detail.fork:Fork`,
    yes: $localize`:@@runs.detail.yes:Yes`,
    no: $localize`:@@runs.detail.no:No`,
    evidence: $localize`:@@runs.detail.evidence:Evidence revision`,
    none: $localize`:@@runs.detail.none:None recorded`,
    context: $localize`:@@runs.detail.context:Execution context`,
    environment: $localize`:@@runs.detail.environment:Environment`,
    revision: $localize`:@@runs.detail.release.environment:Environment revision`,
  };
}
