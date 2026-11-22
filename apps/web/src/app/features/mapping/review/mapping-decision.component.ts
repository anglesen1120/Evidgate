import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmDialog,
  HlmDialogContent,
  HlmDialogDescription,
  HlmDialogFooter,
  HlmDialogHeader,
  HlmDialogPortal,
  HlmDialogTitle,
} from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { mappingCopy } from './mapping-resolution.copy';
import type { MappingPreview, MappingRowViewModel } from './mapping-resolution.view-model';

@Component({
  selector: 'app-mapping-decision',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmDialog,
    HlmDialogContent,
    HlmDialogDescription,
    HlmDialogFooter,
    HlmDialogHeader,
    HlmDialogPortal,
    HlmDialogTitle,
    HlmInput,
    HlmLabel,
  ],
  templateUrl: './mapping-decision.component.html',
})
export class MappingDecisionComponent {
  protected readonly policy = inject(NarrowMutationPolicy);
  readonly row = input.required<MappingRowViewModel>();
  readonly source = input.required<string>();
  readonly mode = input.required<MappingPreview['mode']>();
  readonly confirmed = output<MappingPreview>();
  readonly cancelled = output<void>();
  protected readonly copy = mappingCopy;
  protected readonly finished = signal(false);
  protected readonly search = signal('');
  protected readonly candidate = signal<string | null>(null);
  protected readonly matches = computed(() => {
    const query = this.search().trim().toLocaleLowerCase();
    return this.row().candidates.filter((item) =>
      (item.id + ' ' + item.title).toLocaleLowerCase().includes(query),
    );
  });
  protected readonly canConfirm = computed(
    () =>
      this.policy.canMutate() &&
      !this.finished() &&
      (this.mode() === 'dismiss' || this.matches().some((item) => item.id === this.candidate())),
  );

  protected filter(value: string): void {
    this.search.set(value);
    if (!this.matches().some((item) => item.id === this.candidate())) this.candidate.set(null);
  }

  protected cancel(): void {
    if (this.finished()) return;
    this.finished.set(true);
    this.cancelled.emit();
  }

  protected confirm(): void {
    if (!this.policy.allowsMutation() || !this.canConfirm()) return;
    this.finished.set(true);
    this.confirmed.emit({
      mode: this.mode(),
      mapping: this.row().id,
      candidate: this.mode() === 'bind' ? this.candidate() : null,
    });
  }
}
