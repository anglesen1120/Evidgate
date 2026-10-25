// 09.7.1-T1.1/T1.2 (AC1): empty accessible destinations disclose no hidden destination names.
import { ChangeDetectionStrategy, Component } from '@angular/core';

import { EmptyPatternComponent } from '../../shared/ui/states/empty-pattern.component';

@Component({
  selector: 'app-settings-no-tabs',
  standalone: true,
  imports: [EmptyPatternComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './settings-no-tabs.page.css',
  template: `<app-empty-pattern [title]="title" [description]="description" />`,
})
export class SettingsNoTabsPage {
  protected readonly title = $localize`:@@settings.empty.title:No settings destinations`;
  protected readonly description = $localize`:@@settings.empty.description:No settings destinations are available with your permissions. Ask an administrator.`;
}
