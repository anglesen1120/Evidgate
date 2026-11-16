import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCardDescription, HlmCardTitle, type HlmCardTitleSize } from '@evidgate/ui/card';

@Component({
  selector: 'app-section-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardDescription, HlmCardTitle],
  styleUrl: './header-layout.css',
  template: `
    <div class="header-row">
      <div class="header-copy">
        @if (headingLevel() === 2) {
          <h2 hlmCardTitle [size]="size()" [attr.id]="titleId()">{{ title() }}</h2>
        } @else {
          <h3 hlmCardTitle [size]="size()" [attr.id]="titleId()">{{ title() }}</h3>
        }
        @if (description(); as description) {
          <p hlmCardDescription class="header-description">{{ description }}</p>
        }
      </div>
      <div class="header-actions">
        <ng-content select="[appSectionHeaderActions]" />
      </div>
    </div>
  `,
})
export class SectionHeaderComponent {
  readonly title = input.required<string>();
  readonly description = input<string | undefined>();
  readonly titleId = input<string | undefined>();
  readonly headingLevel = input<2 | 3>(2);
  readonly size = input<Extract<HlmCardTitleSize, 'section' | 'subsection'>>('section');
}
