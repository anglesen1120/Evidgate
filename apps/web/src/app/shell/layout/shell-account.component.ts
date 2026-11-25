import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmDropdownMenu,
  HlmDropdownMenuItem,
  HlmDropdownMenuTrigger,
} from '@evidgate/ui/dropdown-menu';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { SessionStore } from '../../core/session/session.store';
import { ShellIconComponent } from './shell-icon.component';

@Component({
  selector: 'app-shell-account',
  imports: [
    HlmButton,
    HlmDropdownMenu,
    HlmDropdownMenuItem,
    HlmDropdownMenuTrigger,
    HlmTooltip,
    ShellIconComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      hlmBtn
      [variant]="'ghost'"
      [size]="'icon'"
      class="min-h-(--eg-control-touch) min-w-(--eg-control-touch)"
      [hlmTooltip]="accountLabel"
      [hlmDropdownMenuTrigger]="accountMenu"
      [align]="'end'"
      aria-label="Account menu"
      i18n-aria-label="@@shell.account.menu"
    >
      <span
        class="grid size-(--eg-control-sm) shrink-0 place-items-center rounded-full border-(length:--eg-border-width) border-solid border-primary bg-accent text-accent-foreground"
      >
        <app-shell-icon [name]="'account'" />
      </span>
    </button>
    <ng-template #accountMenu>
      <hlm-dropdown-menu>
        <button
          hlmDropdownMenuItem
          type="button"
          class="min-h-(--eg-control-touch)"
          (triggered)="session.logout()"
          i18n="@@shell.account.sign-out"
        >
          Sign out of this tab
        </button>
      </hlm-dropdown-menu>
    </ng-template>
  `,
})
export class ShellAccountComponent {
  protected readonly session = inject(SessionStore);
  protected readonly accountLabel = $localize`:@@shell.account.label:Account`;
}
