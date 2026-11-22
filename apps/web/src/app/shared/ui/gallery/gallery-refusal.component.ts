import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import type { MissingReason } from '../annotations';
import { GALLERY_COPY } from './gallery-example-copy';

@Component({
  selector: 'app-gallery-refusal',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmCheckbox, HlmDialogImports, HlmIcon, HlmInput, HlmLabel],
  host: { class: 'block min-w-0' },
  templateUrl: './gallery-refusal.component.html',
})
export class GalleryRefusalComponent {
  readonly reasons = input.required<readonly MissingReason[]>();
  protected readonly copy = GALLERY_COPY;
  protected readonly runId = 'run-1842';
  protected readonly reasonLabels: Readonly<
    Partial<Record<string, { title: string; hint: string }>>
  > = {
    artifact_missing: {
      title: $localize`:@@shared.gallery.refusal.missing-artifact:Missing artifact`,
      hint: $localize`:@@shared.gallery.refusal.missing-artifact-hint:The expected artifact was not attached.`,
    },
    producer_missing: {
      title: $localize`:@@shared.gallery.refusal.missing-producer:Missing producer evidence`,
      hint: $localize`:@@shared.gallery.reason-plain:The declared producer has not supplied evidence.`,
    },
  };
  protected readonly reason = signal('');
  protected readonly note = signal('');
  protected readonly notify = signal(false);
  protected readonly resubmit = signal(false);
  protected readonly result = signal<{
    code: string;
    note: string;
    notify: boolean;
    resubmit: boolean;
  } | null>(null);
  protected readonly title = $localize`:@@shared.gallery.refusal.title:Decline sample request`;
  protected readonly notifyLabel = $localize`:@@shared.gallery.refusal.notify:Include a requester notification`;
  protected readonly resubmitLabel = $localize`:@@shared.gallery.refusal.resubmit:Allow resubmission`;

  protected prepare(): void {
    this.reason.set('');
    this.note.set('');
    this.notify.set(false);
    this.resubmit.set(false);
  }

  protected decline(dialog: HlmDialog): void {
    const code = this.reason();
    if (!this.reasons().some((reason) => reason.code === code)) return;
    this.result.set({
      code,
      note: this.note().trim(),
      notify: this.notify(),
      resubmit: this.resubmit(),
    });
    dialog.close();
  }
}
