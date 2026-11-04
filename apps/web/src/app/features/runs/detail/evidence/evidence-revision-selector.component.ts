import { formatDate } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
  output,
} from '@angular/core';
import type { EvidenceRevision } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { BrnSelectTrigger } from '@spartan-ng/brain/select';

/** Selection and pagination are intents; only the page decides which revision is authoritative. */
@Component({
  selector: 'app-evidence-revision-selector',
  imports: [HlmButton, HlmIcon, HlmLabel, HlmSelectImports, BrnSelectTrigger],
  host: { class: 'block min-w-0' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './evidence-revision-selector.component.html',
})
export class EvidenceRevisionSelectorComponent {
  readonly revisions = input.required<readonly EvidenceRevision[]>();
  readonly selectedRevision = input<number | null>(null);
  /** More cursor pages exist: "Load older revisions" is offered, never automatic. */
  readonly hasMore = input(false);
  readonly loadingMore = input(false);
  readonly selectRevision = output<number>();
  readonly loadOlder = output<void>();

  private readonly locale = inject(LOCALE_ID);

  protected readonly copy = {
    revisionLabel: $localize`:@@runs.evidence.revision-label:Evidence revision`,
    pinHelp: $localize`:@@runs.evidence.pin-help:This view stays pinned to the selected evidence revision.`,
    loadOlder: $localize`:@@runs.evidence.load-older:Load older revisions`,
    loadingOlder: $localize`:@@runs.evidence.loading-older:Loading older revisions`,
  };

  /** The pin as the control's own value, so the control never shows a revision it is not pinned to. */
  protected readonly selectedValue = computed(() => {
    const pinned = this.selectedRevision();
    return pinned === null ? null : String(pinned);
  });

  /** The pin is not on a loaded page: a disabled placeholder stands in for it, never another row. */
  protected readonly pinnedMissing = computed(() => {
    const pinned = this.selectedRevision();
    return pinned !== null && !this.revisions().some((row) => row.revision_no === pinned);
  });

  protected readonly notLoadedLabel = computed(() => {
    const pinned = this.selectedRevision();
    return pinned === null
      ? ''
      : $localize`:@@runs.evidence.revision-not-loaded:Revision ${pinned}:revision: (not loaded)`;
  });

  /** `Revision {revision} · {createdAt} UTC`: the number is the key, the time is context. */
  protected optionLabel(revision: EvidenceRevision): string {
    const createdAt = formatDate(revision.created_at, 'short', this.locale, 'UTC');
    return $localize`:@@runs.evidence.revision-option:Revision ${revision.revision_no}:revision: · ${createdAt}:createdAt: UTC`;
  }

  protected readonly selectedLabel = computed(() => {
    const selected = this.revisions().find((row) => row.revision_no === this.selectedRevision());
    return selected ? this.optionLabel(selected) : this.notLoadedLabel();
  });

  protected choose(value: unknown): void {
    const revision = Number(value);
    if (this.revisions().some((row) => row.revision_no === revision)) {
      this.selectRevision.emit(revision);
    }
  }
}
