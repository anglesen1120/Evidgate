import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmDialog,
  HlmDialogClose,
  HlmDialogContent,
  HlmDialogFooter,
  HlmDialogHeader,
  HlmDialogPortal,
  HlmDialogTitle,
} from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { StatusPillComponent } from '../../../shared/ui/chips';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import {
  DecisionChipComponent,
  EvidenceBadgeComponent,
  OutcomePillComponent,
} from '../../../shared/ui/verdict';
import { mappingCopy } from './mapping-resolution.copy';
import type {
  MappingResolutionViewModel,
  MappingRowViewModel,
} from './mapping-resolution.view-model';

@Component({
  selector: 'app-mapping-record-view',
  imports: [
    HlmBadge,
    HlmButton,
    HlmDialog,
    HlmDialogClose,
    HlmDialogContent,
    HlmDialogFooter,
    HlmDialogHeader,
    HlmDialogPortal,
    HlmDialogTitle,
    HlmIcon,
    StatusPillComponent,
    DesktopRequiredNoticeComponent,
    DecisionChipComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './mapping-record-view.component.html',
})
export class MappingRecordViewComponent {
  readonly row = input.required<MappingRowViewModel>();
  readonly context = input.required<MappingResolutionViewModel>();
  readonly opener = input.required<HTMLButtonElement>();
  readonly readOnly = input.required<boolean>();
  readonly dismissed = output<void>();
  protected readonly copy = mappingCopy;
  protected readonly display = escapeFormatChars;
  protected readonly facts = computed(() => {
    const vm = this.context();
    return [
      [this.copy.sourceName, this.row().name, true],
      [this.copy.producerSuite, vm.producer + ' / ' + vm.suite, true],
      [this.copy.original, this.copy.attempt, false],
      [this.copy.suggestion, this.row().suggestion, true],
      [this.copy.runReport, vm.run + ' / ' + vm.report, true],
      [this.copy.frozen, vm.evidence, true],
      [this.copy.digest, vm.digest, true],
    ] as const;
  });
}
