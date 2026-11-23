import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { galleryCopy } from './studio-gallery.copy';
import type { GalleryTemplateViewModel } from './studio-gallery.fixture';

@Component({
  selector: 'app-gallery-template-view',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmBadge, HlmButton, HlmDialogImports, HlmIcon],
  template: `
    <hlm-dialog
      [state]="'open'"
      [autoFocus]="'[data-gallery-view-title]'"
      [restoreFocus]="false"
      (closed)="dismissed.emit()"
    >
      <hlm-dialog-content *hlmDialogPortal [size]="'lg'">
        <header hlmDialogHeader>
          <h2 hlmDialogTitle tabindex="-1" data-gallery-view-title>
            <bdi>{{ item().title }}</bdi>
          </h2>
          <div class="flex flex-wrap items-center gap-2">
            <code hlmBadge [variant]="'outline'"
              ><bdi>{{ escapeFormatChars(item().engine) }}</bdi></code
            >
            <span hlmBadge [variant]="'secondary'"
              ><hlm-icon name="lucideInfo" />{{
                item().source === 'built' ? copy.builtBadge : copy.teamBadge
              }}</span
            >
          </div>
        </header>
        <dl class="hlm-field-grid">
          <div>
            <dt>{{ copy.engineVariant }}</dt>
            <dd>
              <span class="block"
                ><bdi>{{ escapeFormatChars(item().engine) }}</bdi></span
              ><code
                ><bdi>{{ escapeFormatChars(item().variant) }}</bdi></code
              >
            </dd>
          </div>
          <div class="hlm-field-wide">
            <dt>{{ copy.intent }}</dt>
            <dd>
              <bdi>{{ escapeFormatChars(item().intent) }}</bdi>
            </dd>
          </div>
          <div class="hlm-field-wide">
            <dt>{{ copy.precondition }}</dt>
            <dd>
              <bdi>{{ escapeFormatChars(item().precondition) }}</bdi>
            </dd>
          </div>
          <div class="hlm-field-wide">
            <dt>{{ copy.action }}</dt>
            <dd>
              <bdi>{{ escapeFormatChars(item().action) }}</bdi>
            </dd>
          </div>
          <div class="hlm-field-wide">
            <dt>{{ copy.assertions }}</dt>
            <dd>
              <bdi>{{ escapeFormatChars(item().assertion) }}</bdi>
            </dd>
          </div>
          <div class="hlm-field-wide">
            <dt>{{ copy.cleanup }}</dt>
            <dd>{{ item().cleanup }}</dd>
          </div>
        </dl>
        <footer hlmDialogFooter>
          <button hlmBtn [variant]="'outline'" type="button" hlmDialogClose>
            {{ copy.close }}
          </button>
          @if (!readOnly()) {
            <button
              hlmBtn
              type="button"
              data-gallery-view-create
              [disabled]="createDisabled()"
              (click)="createRequested.emit()"
            >
              {{ copy.create
              }}<span class="sr-only"
                >&ngsp;<bdi>{{ item().title }}</bdi></span
              >
            </button>
          }
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class GalleryTemplateViewComponent {
  readonly item = input.required<GalleryTemplateViewModel>();
  readonly readOnly = input(false);
  readonly createDisabled = input(false);
  readonly createRequested = output<void>();
  readonly dismissed = output<void>();
  protected readonly copy = galleryCopy;
  protected readonly escapeFormatChars = escapeFormatChars;
}
