import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmSkeleton } from '@evidgate/ui/skeleton';

@Component({
  selector: 'app-loading-pattern',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCard, HlmCardTitle, HlmSkeleton],
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="state-panel state-panel--loading min-w-0 min-h-[calc(var(--eg-space-12)*3+var(--eg-space-8))] items-center justify-center gap-(--eg-space-3) px-(--eg-space-5) py-(--eg-space-8) text-center wrap-anywhere"
      role="status"
      [attr.aria-live]="'polite'"
      aria-busy="true"
    >
      <span
        hlmSkeleton
        class="loading-pattern__skeleton size-(--eg-control-touch) rounded-full"
        aria-hidden="true"
      ></span>
      <h2 hlmCardTitle class="state-panel__title m-0 font-(family-name:--eg-font-display) text-lg">
        @if (label(); as suppliedLabel) {
          {{ suppliedLabel }}
        } @else {
          <span i18n="@@shared.states.loading.label">Loading evidence</span>
        }
      </h2>
    </section>
  `,
})
export class LoadingPatternComponent {
  readonly label = input<string | undefined>();
}
