import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'app-loading-pattern',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['./state-panel.css', './loading-pattern.component.css'],
  template: `
    <section
      class="state-panel state-panel--loading"
      role="status"
      [attr.aria-live]="'polite'"
      aria-busy="true"
    >
      <span class="loading-pattern__skeleton" aria-hidden="true"></span>
      <h2 class="state-panel__title">
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
