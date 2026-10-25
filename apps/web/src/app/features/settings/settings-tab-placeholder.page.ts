// 09.7.1-T1.1 (D-S68-Q3): identify a destination, never a resource or fabricated result.
import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';

import { SETTINGS_TABS } from './settings-tabs';

@Component({
  selector: 'app-settings-tab-placeholder',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { '[attr.data-settings-tab-body]': 'tab?.id ?? null' },
  styleUrls: ['../../shell/layout/shell-page.css', './settings-tab-placeholder.page.css'],
  template: `
    @if (tab; as tab) {
      <section>
        <h2>{{ tab.label }}</h2>
        <p i18n="@@settings.placeholder.body">This tab arrives with its own feature story.</p>
      </section>
    }
  `,
})
export class SettingsTabPlaceholderPage {
  private readonly data: Readonly<Record<string, unknown>> = inject(ActivatedRoute).snapshot.data;
  protected readonly tab = SETTINGS_TABS.find((tab) => tab.id === this.data['settingsTabId']);
}
