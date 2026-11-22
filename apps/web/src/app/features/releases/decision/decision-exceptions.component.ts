import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { WaiverBanner } from '../../../shared/ui/annotations';
import { StatusPillComponent } from '../../../shared/ui/chips';
import { DesktopMutationDirective } from '../../../shared/ui/narrow';
import { InlineFeedbackComponent } from '../../../shared/ui/states';
import { DecisionPreview } from './decision-preview.component';
import { decisionCopy } from './decision-review.copy';
import {
  runFacts,
  type ExceptionRecord,
  type ReleaseDecisionViewModel,
} from './decision-review.fixture';

const RecordSelection = signalStore(
  withState<{ id: ExceptionRecord['id'] | null }>({ id: null }),
  withMethods((store) => ({
    select(id: ExceptionRecord['id']) {
      patchState(store, { id });
    },
  })),
);

@Component({
  selector: 'app-decision-exceptions',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmCard,
    WaiverBanner,
    StatusPillComponent,
    InlineFeedbackComponent,
    DecisionPreview,
    DesktopMutationDirective,
  ],
  providers: [RecordSelection],
  templateUrl: './decision-exceptions.component.html',
  host: { class: 'block min-w-0 wrap-anywhere' },
})
export class DecisionExceptions {
  protected readonly selection = inject(RecordSelection);
  readonly view = input.required<ReleaseDecisionViewModel>();
  protected readonly copy = decisionCopy;
  protected readonly facts = runFacts;
  protected readonly current = computed(() =>
    this.view().records.find((r) => r.id === (this.selection.id() ?? this.view().initialRecord)),
  );
}
