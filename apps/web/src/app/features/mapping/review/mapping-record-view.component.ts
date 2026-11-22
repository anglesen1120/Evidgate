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
  styles: `
    .mapping-view {
      width: min(560px, calc(100vw - var(--eg-space-8)));
      max-width: 100vw;
      max-height: calc(100dvh - var(--eg-space-8));
      grid-template-rows: auto minmax(0, 1fr) auto;
      overflow: hidden;
      gap: 0;
      padding: 0;
    }
    @media (width < 600px) {
      .mapping-view {
        width: 100vw;
        height: 100dvh;
        max-height: 100dvh;
        border: 0;
        border-radius: 0;
        padding: env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom)
          env(safe-area-inset-left);
      }
    }
  `,
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
      [this.copy.sourceName, this.row().name],
      [this.copy.producerSuite, vm.producer + ' / ' + vm.suite],
      [this.copy.original, this.copy.attempt],
      [this.copy.suggestion, this.row().suggestion],
      [this.copy.runReport, vm.run + ' / ' + vm.report],
      [this.copy.frozen, vm.evidence],
      [this.copy.digest, vm.digest],
    ];
  });
}
