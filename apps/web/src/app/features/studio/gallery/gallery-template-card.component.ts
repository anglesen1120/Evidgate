import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardDescription, HlmCardTitle } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { galleryCopy } from './studio-gallery.copy';
import { type GalleryTemplateViewModel } from './studio-gallery.fixture';

@Component({
  selector: 'app-gallery-template-card',
  imports: [HlmBadge, HlmButton, HlmCard, HlmCardDescription, HlmCardTitle, HlmIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './gallery-template-card.component.css',
  template: `
    <article hlmCard [class.selected]="selected()" [attr.aria-labelledby]="'gallery-' + item().id">
      <div class="illustration" [attr.data-kind]="item().id" aria-hidden="true">
        <div class="specimen">
          <code>{{ item().illustration[0] }}</code>
          <strong>{{ item().illustration[1] }}</strong>
        </div>
      </div>
      <div class="body min-w-0 wrap-anywhere">
        <div class="meta flex flex-wrap items-center justify-between">
          <code>{{ item().engine }}</code>
          <span hlmBadge [variant]="'secondary'"
            ><hlm-icon name="lucideInfo" />{{
              item().source === 'built' ? copy.builtBadge : copy.teamBadge
            }}</span
          >
        </div>
        <h2 hlmCardTitle [size]="'subsection'" [id]="'gallery-' + item().id">{{ item().title }}</h2>
        <p hlmCardDescription>{{ item().intent }}</p>
        <div>
          <h3>{{ copy.assertions }}</h3>
          <p hlmCardDescription>{{ item().assertion }}</p>
        </div>
        <button
          hlmBtn
          [variant]="'outline'"
          type="button"
          [attr.aria-controls]="'gallery-preview'"
          [attr.aria-pressed]="selected()"
          [attr.aria-describedby]="'gallery-' + item().id"
          (click)="picked.emit(item().id)"
        >
          @if (selected()) {
            <hlm-icon name="lucideCheck" class="size-4" />
          }
          {{ copy.pick }}
        </button>
      </div>
    </article>
  `,
})
export class GalleryTemplateCardComponent {
  readonly item = input.required<GalleryTemplateViewModel>();
  readonly selected = input.required<boolean>();
  readonly picked = output<GalleryTemplateViewModel['id']>();
  protected readonly copy = galleryCopy;
}
