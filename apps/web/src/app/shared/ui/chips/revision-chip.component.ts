import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';

@Component({
  selector: 'app-revision-chip',
  template: `
    <span class="identity-chip revision-chip">
      @if (hasRevision()) {
        @if (label()) {
          <span class="identity-chip__label">{{ label() }}</span>
        }
        <span class="identity-chip__value">{{ identity() }}</span>
      } @else {
        <span class="identity-chip__label" i18n="@@shared.revision-chip.unavailable">
          Revision unavailable
        </span>
      }
    </span>
  `,
  styleUrl: './identity-chip.css',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class RevisionChipComponent {
  readonly revision = input.required<string | number>();
  /** Localized field context; callers can distinguish evidence, policy or case revision. */
  readonly label = input($localize`:@@shared.revision-chip.label:Revision`);

  protected readonly hasRevision = computed(() => {
    const revision = this.revision();
    if (typeof revision === 'number') {
      return Number.isInteger(revision) && revision > 0;
    }
    return revision !== null && revision !== undefined && String(revision).trim().length > 0;
  });
  // Keep the supplied identifier locale-independent; r is an identity prefix, not prose.
  protected readonly identity = computed(() => `r${this.revision()}`);
}
