import { ChangeDetectionStrategy, Component, ElementRef, input, viewChild } from '@angular/core';
import { HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';
import { HlmIcon, type HlmIconName } from '@evidgate/ui/icon';

@Component({
  selector: 'app-page-header',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardDescription, HlmCardTitle, HlmIcon],
  styleUrl: './header-layout.css',
  template: `
    <div class="header-context">
      <ng-content select="[pageHeaderContext]" />
    </div>
    <div class="header-row">
      <div class="header-copy">
        <h1
          #heading
          hlmCardTitle
          tabindex="-1"
          [size]="titleSize"
          [attr.id]="titleId()"
          [class.header-title-with-icon]="icon()"
        >
          @if (icon(); as name) {
            <hlm-icon [name]="name" class="size-(--eg-control-sm)" />
          }
          {{ title() }}
        </h1>
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
  readonly icon = input<HlmIconName>();
  readonly description = input<string | undefined>();
  readonly titleId = input<string | undefined>();
  readonly titleSize = 'page' as const;
  private readonly heading = viewChild.required<ElementRef<HTMLHeadingElement>>('heading');

  focusTitle(): void {
    this.heading().nativeElement.focus();
  }
}
