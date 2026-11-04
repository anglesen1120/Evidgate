import { ChangeDetectionStrategy, Component } from '@angular/core';

import { EmptyPatternComponent } from '../../shared/ui/states/empty-pattern.component';

@Component({
  selector: 'app-settings-no-tabs',
  imports: [EmptyPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `<app-empty-pattern [title]="title" [description]="description" />`,
})
export class SettingsNoTabsPage {
  protected readonly title = $localize`:@@settings.empty.title:No settings destinations`;
  protected readonly description = $localize`:@@settings.empty.description:No settings destinations are available with your permissions. Ask an administrator.`;
}
