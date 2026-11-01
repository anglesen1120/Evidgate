import { formatDate } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  inject,
  input,
  LOCALE_ID,
  output,
} from '@angular/core';
import type { EvidenceRevision } from '@evidgate/sdk';

/**
 * The native revision picker: one labelled `<select>`, the pin guidance, and the explicit older-page
 * action. It renders loaded rows only and never decides which revision is authoritative; selection
 * and pagination are emitted as intents the page turns into store calls.
 */
@Component({
  selector: 'app-evidence-revision-selector',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './evidence-revision-selector.component.html',
  styleUrl: './evidence-revision-selector.component.css',
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

  /** `Revision {revision} · {createdAt} UTC`: the number is the key, the time is context. */
  protected optionLabel(revision: EvidenceRevision): string {
    const createdAt = formatDate(revision.created_at, 'short', this.locale, 'UTC');
    return $localize`:@@runs.evidence.revision-option:Revision ${revision.revision_no}:revision: · ${createdAt}:createdAt: UTC`;
  }

  protected choose(event: Event): void {
    if (!(event.target instanceof HTMLSelectElement)) return;
    const revision = Number(event.target.value);
    if (Number.isInteger(revision) && revision > 0) this.selectRevision.emit(revision);
  }
}
