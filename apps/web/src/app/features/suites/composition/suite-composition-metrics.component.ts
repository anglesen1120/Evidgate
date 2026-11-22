import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import type { SuiteCompositionView } from './suite-composition.model';

export const PINNED_MEMBERS = $localize`:@@suites.composition.members:Pinned members`;

@Component({
  selector: 'app-suite-composition-metrics',
  imports: [ShellIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'col-span-full block min-w-0' },
  template: `
    <dl
      class="grid min-w-0 grid-cols-2 divide-border rounded-(--eg-radius-md) border border-border bg-card sm:grid-cols-4 sm:divide-x [&>div]:grid [&>div]:content-between [&>div]:gap-(--eg-space-2) [&>div]:p-(--eg-space-5) [&_dt]:flex [&_dt]:items-center [&_dt]:gap-(--eg-space-3) [&_dt]:text-xs [&_dd]:text-end [&_dd]:text-2xl [&_dd]:font-semibold [&_dd]:tabular-nums"
    >
      <div>
        <dt><app-shell-icon name="list" />{{ members }}</dt>
        <dd>{{ view().flatMembers.length }}</dd>
      </div>
      <div>
        <dt><app-shell-icon name="file" />{{ cases }}</dt>
        <dd>{{ view().rows.length }}</dd>
      </div>
      <div>
        <dt><app-shell-icon name="layers" />{{ variants }}</dt>
        <dd>{{ view().columns.length }}</dd>
      </div>
      <div>
        <dt><app-shell-icon name="tree" />{{ dependencies }}</dt>
        <dd>{{ view().dependencies.length }}</dd>
      </div>
    </dl>
  `,
})
export class SuiteCompositionMetricsComponent {
  readonly view = input.required<SuiteCompositionView>();
  protected readonly members = PINNED_MEMBERS;
  protected readonly cases = $localize`:@@suites.composition.case-count:Case revisions`;
  protected readonly variants = $localize`:@@suites.composition.variants:Variants`;
  protected readonly dependencies = $localize`:@@suites.composition.dependencies:Dependencies`;
}
