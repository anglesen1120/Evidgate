import { ChangeDetectionStrategy, Component, inject, resource } from '@angular/core';
import { HlmDropdownMenuLabel } from '@evidgate/ui/dropdown-menu';
import { IdentityDataService } from '../../core/data/identity-data.service';

/** The portal owns this read so closing the menu discards both the request and the name. */
@Component({
  selector: 'app-shell-account-name',
  imports: [HlmDropdownMenuLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div hlmDropdownMenuLabel class="text-foreground [overflow-wrap:anywhere]">
      @if (name.hasValue() && name.value(); as displayName) {
        <bdi>{{ displayName }}</bdi>
      } @else {
        <span i18n="@@shell.account.label">Account</span>
      }
    </div>
  `,
})
export class ShellAccountNameComponent {
  private readonly identity = inject(IdentityDataService);
  protected readonly name = resource({
    loader: async ({ abortSignal }) => (await this.identity.getMyProfile(abortSignal)).display_name,
  });
}
