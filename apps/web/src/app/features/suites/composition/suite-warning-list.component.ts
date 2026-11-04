import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmAlert } from '@evidgate/ui/alert';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';

@Component({
  selector: 'app-suite-warning-list',
  imports: [HlmAlert, HlmCard, HlmCardTitle],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './suite-warning-list.component.html',
  host: { class: 'block min-w-0' },
})
export class SuiteWarningListComponent {
  readonly warnings = input<readonly string[] | null | undefined>();

  protected readonly copy = {
    title: $localize`:@@suites.warnings.suite-warnings:Suite warnings`,
    unavailable: $localize`:@@suites.warnings.warnings-unavailable:Warnings unavailable`,
    unavailableReason: $localize`:@@suites.warnings.the-suite-revision-response-does-not-include-war:The suite revision response does not include warning details.`,
    empty: $localize`:@@suites.warnings.no-warnings-reported:No warnings reported.`,
  };
}
