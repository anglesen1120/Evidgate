import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { API_TOKENS_COPY } from './api-tokens.copy';

@Component({
  selector: 'app-api-token-permission-tags',
  imports: [HlmBadge, HlmButton, HlmDialogImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <div class="flex min-w-0 flex-nowrap items-center gap-[var(--eg-space-1)]">
      @for (permission of preview(); track permission) {
        <span hlmBadge [variant]="'secondary'" [fullValue]="permission">{{ permission }}</span>
      }
      @if (permissions().length > 2) {
        <hlm-dialog>
          <button
            hlmBtn
            hlmDialogTrigger
            [variant]="'ghost'"
            type="button"
            class="shrink-0"
            data-api-permissions-more
            [attr.aria-label]="moreLabel()"
          >
            +{{ permissions().length - 2 }}
          </button>
          <hlm-dialog-content *hlmDialogPortal [closeLabel]="closeLabel">
            <h3 hlmDialogTitle>{{ title }}</h3>
            <p hlmDialogDescription class="wrap-anywhere">{{ name() }}</p>
            <ul class="m-0 grid min-w-0 gap-[var(--eg-space-2)] ps-[var(--eg-space-6)]">
              @for (permission of permissions(); track permission) {
                <li class="wrap-anywhere">
                  <code>{{ permission }}</code>
                </li>
              }
            </ul>
          </hlm-dialog-content>
        </hlm-dialog>
      }
    </div>
  `,
})
export class ApiTokenPermissionTagsComponent {
  readonly permissions = input.required<readonly string[]>();
  readonly name = input.required<string>();
  protected readonly preview = computed(() => this.permissions().slice(0, 2));
  protected readonly title = API_TOKENS_COPY.permissions;
  protected readonly closeLabel = $localize`:@@settings.api-tokens.permissions-close:Close permissions`;
  protected readonly moreLabel = computed(() => {
    const count = this.permissions().length - 2;
    const name = this.name();
    return $localize`:@@settings.api-tokens.permissions-more:Show ${count}:count: more permissions for ${name}:name:`;
  });
}
