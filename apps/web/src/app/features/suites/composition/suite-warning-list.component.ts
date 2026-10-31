import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'app-suite-warning-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './suite-warning-list.component.html',
  styleUrl: './suite-warning-list.component.css',
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
