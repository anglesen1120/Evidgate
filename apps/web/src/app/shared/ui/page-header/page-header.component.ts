import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';

@Component({
  selector: 'app-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardDescription, HlmCardTitle],
  styleUrl: './header-layout.css',
  template: `
    <div class="header-context">
      <ng-content select="[pageHeaderContext]" />
    </div>
    <div class="header-row">
      <div class="header-copy">
        <h1 hlmCardTitle [size]="titleSize" [attr.id]="titleId()">{{ title() }}</h1>
        @if (description(); as description) {
          <p hlmCardDescription class="header-description eg-type-body">{{ description }}</p>
        }
      </div>
      <div class="header-actions">
        <ng-content select="[appPageHeaderActions]" />
      </div>
    </div>
  `,
})
export class PageHeaderComponent {
  readonly title = input.required<string>();
  readonly description = input<string | undefined>();
  readonly titleId = input<string | undefined>();
  readonly titleSize = 'page' as const;
}
