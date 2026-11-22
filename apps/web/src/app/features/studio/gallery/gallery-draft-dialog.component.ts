import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  input,
  linkedSignal,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { galleryCopy } from './studio-gallery.copy';
import { type GalleryTemplateViewModel } from './studio-gallery.fixture';

export type GalleryDraftMode = 'preview' | 'invalid' | 'createerror' | 'busy';
@Component({
  selector: 'app-gallery-draft-dialog',
  imports: [
    FormsModule,
    HlmBadge,
    HlmIcon,
    HlmButton,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    DesktopRequiredNoticeComponent,
    InlineFeedbackComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './gallery-draft-dialog.component.html',
  styleUrls: ['./gallery-record-dialog.css', './gallery-draft-dialog.component.css'],
  host: { '(window:beforeunload)': 'onBeforeUnload($event)' },
})
export class GalleryDraftDialogComponent {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly dialog = viewChild.required(HlmDialog);
  readonly templates = input.required<readonly GalleryTemplateViewModel[]>();
  readonly initial = input.required<GalleryTemplateViewModel>();
  readonly mode = input.required<GalleryDraftMode>();
  readonly readOnly = input(false);
  readonly dismissed = output<void>();
  readonly historical = output<void>();
  protected readonly copy = galleryCopy;
  protected readonly confirmation = signal<'close' | GalleryTemplateViewModel['id'] | null>(null);
  private previousFieldId = 'gallery-draft-name';
  private closing = false;
  protected readonly item = linkedSignal(() => this.initial());
  protected readonly draftName = linkedSignal(() => this.item().draftName);
  protected readonly intent = linkedSignal(() => this.item().intent);
  protected readonly assertion = linkedSignal(() =>
    this.mode() === 'invalid' && this.item() === this.initial() ? '' : this.item().assertion,
  );
  protected readonly noResult = signal(false);
  protected readonly busy = computed(() => this.mode() === 'busy');
  protected readonly fieldsLocked = computed(() => this.busy() || this.readOnly());
  protected readonly invalid = computed(() => !this.assertion().trim());
  protected readonly missingText = computed(
    () => !this.draftName().trim() || !this.intent().trim(),
  );
  protected readonly blocked = computed(
    () => this.fieldsLocked() || this.invalid() || this.missingText(),
  );
  private readonly editedFields = computed(() => {
    const item = this.item();
    const initialAssertion =
      this.mode() === 'invalid' && item === this.initial() ? '' : item.assertion;
    return (
      this.draftName() !== item.draftName ||
      this.intent() !== item.intent ||
      this.assertion() !== initialAssertion
    );
  });
  private readonly dirty = computed(
    () => this.editedFields() || this.item().id !== this.initial().id,
  );
  protected readonly titleFor = (id: unknown) =>
    this.templates().find((item) => item.id === id)?.title ?? '';

  protected select(id: unknown): void {
    const next = this.templates().find((item) => item.id === id);
    if (!next || this.fieldsLocked() || next === this.item()) return;
    if (this.editedFields()) {
      this.confirmDiscard(next.id);
      return;
    }
    this.item.set(next);
    this.noResult.set(false);
  }

  protected onBeforeUnload(event: Event): void {
    if (this.dirty() && !this.closing) event.preventDefault();
  }

  protected rememberField(event: FocusEvent): void {
    const field = event.target;
    if (field instanceof HTMLElement && field.id) this.previousFieldId = field.id;
  }

  protected dismiss(): void {
    if (this.dirty()) this.confirmDiscard('close');
    else this.close();
  }

  protected onStateChanged(state: 'closed' | 'open'): void {
    if (state !== 'closed' || this.closing || !this.dirty()) return;
    // Brain signals dismissal before it disposes the overlay. Reopen this trap rather than nesting another.
    this.dialog().open();
    if (!this.confirmation()) this.confirmDiscard('close');
  }

  private confirmDiscard(next: 'close' | GalleryTemplateViewModel['id']): void {
    this.confirmation.set(next);
    this.focusAfterRender('gallery-keep');
  }

  protected keepEditingOnEscape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.keepEditing();
  }

  protected keepEditing(): void {
    this.confirmation.set(null);
    this.focusAfterRender(this.previousFieldId);
  }

  protected discard(): void {
    const target = this.confirmation();
    if (target === 'close') this.close();
    else {
      const next = this.templates().find((item) => item.id === target);
      if (!next) return;
      this.item.set(next);
      this.noResult.set(false);
      this.confirmation.set(null);
      this.focusAfterRender('gallery-form-template');
    }
  }

  private focusAfterRender(id: string): void {
    afterNextRender(() => this.document.getElementById(id)?.focus(), { injector: this.injector });
  }

  private close(): void {
    this.closing = true;
    this.dialog().close();
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
