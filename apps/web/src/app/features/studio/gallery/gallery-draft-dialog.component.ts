import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  output,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { galleryCopy } from './studio-gallery.copy';
import { type GalleryTemplateViewModel } from './studio-gallery.fixture';

export type GalleryDraftMode = 'preview' | 'invalid' | 'createerror' | 'busy';
@Component({
  selector: 'app-gallery-draft-dialog',
  imports: [
    FormsModule,
    HlmButton,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    InlineFeedbackComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './gallery-draft-dialog.component.html',
  styleUrl: './gallery-draft-dialog.component.css',
})
export class GalleryDraftDialogComponent {
  readonly templates = input.required<readonly GalleryTemplateViewModel[]>();
  readonly initial = input.required<GalleryTemplateViewModel>();
  readonly mode = input.required<GalleryDraftMode>();
  readonly dismissed = output<void>();
  readonly historical = output<void>();
  protected readonly copy = galleryCopy;
  protected readonly open = signal(true);
  protected readonly item = linkedSignal(() => this.initial());
  protected readonly draftName = linkedSignal(() => this.item().draftName);
  protected readonly intent = linkedSignal(() => this.item().intent);
  protected readonly assertion = linkedSignal(() =>
    this.mode() === 'invalid' && this.item() === this.initial() ? '' : this.item().assertion,
  );
  protected readonly noResult = signal(false);
  protected readonly busy = computed(() => this.mode() === 'busy');
  protected readonly invalid = computed(() => !this.assertion().trim());
  protected readonly missingText = computed(
    () => !this.draftName().trim() || !this.intent().trim(),
  );
  protected readonly blocked = computed(() => this.busy() || this.invalid() || this.missingText());
  protected readonly titleFor = (id: unknown) =>
    this.templates().find((item) => item.id === id)?.title ?? '';

  protected select(id: unknown): void {
    const next = this.templates().find((item) => item.id === id);
    if (!next || this.busy()) return;
    this.item.set(next);
    this.noResult.set(false);
  }

  protected useTemplate(): void {
    if (this.blocked()) return;
    const item = this.item();
    if (
      item.id === 'checkout' &&
      this.draftName() === item.draftName &&
      this.intent() === item.intent &&
      this.assertion() === item.assertion
    ) {
      this.historical.emit();
    } else {
      this.noResult.set(true);
    }
  }
}
