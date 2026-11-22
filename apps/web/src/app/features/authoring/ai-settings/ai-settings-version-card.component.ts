import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { AiSettingsView } from '@evidgate/sdk';
import { HlmCardContent, HlmCardHeader, HlmCardTitle } from '@evidgate/ui/card';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';

@Component({
  selector: 'app-ai-settings-version-card',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmCardContent,
    HlmCardHeader,
    HlmCardTitle,
    AbsoluteTimeComponent,
    CopyableIdentifierComponent,
  ],
  template: `
    @let view = version();
    <header hlmCardHeader class="border-b border-border">
      <h2 hlmCardTitle [size]="'subsection'" i18n="@@authoring.ai-settings.version-in-use">
        Version in use
      </h2>
    </header>
    <div hlmCardContent class="grid min-w-0 gap-(--eg-space-4)">
      <p class="m-0 font-semibold tabular-nums" i18n="@@authoring.ai-settings.version">
        Current version: {{ view.version_no }}
      </p>
      @if (view.version_no === 0) {
        <p class="m-0 text-muted-foreground" i18n="@@authoring.ai-settings.default">
          Using default guidance; no saved version exists yet.
        </p>
      }
      @if (view.author || view.created_at) {
        <dl class="m-0 grid min-w-0 gap-(--eg-space-3)">
          @if (view.author; as author) {
            <div class="grid min-w-0 gap-(--eg-space-2) border-t border-border pt-(--eg-space-3)">
              <dt class="eg-type-caption text-muted-foreground">{{ authorLabel }}</dt>
              <dd class="m-0 min-w-0">
                <app-copyable-identifier [value]="author" [label]="authorLabel" />
              </dd>
            </div>
          }
          @if (view.created_at; as instant) {
            <div class="grid min-w-0 gap-(--eg-space-2) border-t border-border pt-(--eg-space-3)">
              <dt class="eg-type-caption text-muted-foreground" i18n="@@authoring.ai-settings.time">
                Saved at (UTC)
              </dt>
              <dd class="m-0"><app-absolute-time [value]="instant" /></dd>
            </div>
          }
        </dl>
      }
    </div>
  `,
})
export class AiSettingsVersionCardComponent {
  readonly version = input.required<AiSettingsView>();
  protected readonly authorLabel = $localize`:@@authoring.ai-settings.author:Author membership`;
}
