import { ChangeDetectionStrategy, Component } from '@angular/core';
import { HlmCard } from '@evidgate/ui/card';
import { EmptyPatternComponent } from '../../shared/ui/states/empty-pattern.component';

@Component({
  selector: 'app-settings-no-tabs',
  imports: [HlmCard, EmptyPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section hlmCard class="min-w-0 min-h-60 justify-start rounded-xl p-5">
      <app-empty-pattern [bare]="true" [title]="title" [description]="description" />
    </section>
  `,
})
export class SettingsNoTabsPage {
  protected readonly title = $localize`:@@settings.empty.title:No settings destinations`;
  protected readonly description = $localize`:@@settings.empty.description:No settings destinations are available with your permissions. Ask an administrator.`;
}
