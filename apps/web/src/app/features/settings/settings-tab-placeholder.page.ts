import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { ActivatedRoute } from '@angular/router';
import { HlmCardImports } from '@evidgate/ui/card';

import { SETTINGS_TABS } from './settings-tabs';

@Component({
  selector: 'app-settings-tab-placeholder',
  imports: [HlmCardImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0', '[attr.data-settings-tab-body]': 'tab?.id ?? null' },
  template: `
    @if (tab; as tab) {
      <section hlmCard class="gap-3 p-4 wrap-anywhere">
        <h2 hlmCardTitle>{{ tab.label }}</h2>
        <p hlmCardDescription i18n="@@settings.placeholder.body">
          This tab arrives with its own feature story.
        </p>
      </section>
    }
  `,
})
export class SettingsTabPlaceholderPage {
  private readonly data: Readonly<Record<string, unknown>> = inject(ActivatedRoute).snapshot.data;
  protected readonly tab = SETTINGS_TABS.find((tab) => tab.id === this.data['settingsTabId']);
}
