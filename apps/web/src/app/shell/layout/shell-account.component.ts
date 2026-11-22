import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmDropdownMenu,
  HlmDropdownMenuItem,
  HlmDropdownMenuTrigger,
} from '@evidgate/ui/dropdown-menu';
import { SessionStore } from '../../core/session/session.store';
import { ShellIconComponent } from './shell-icon.component';

@Component({
  selector: 'app-shell-account',
  imports: [
    HlmButton,
    HlmDropdownMenu,
    HlmDropdownMenuItem,
    HlmDropdownMenuTrigger,
    ShellIconComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      type="button"
      hlmBtn
      [variant]="'ghost'"
      class="min-h-(--eg-control-touch) min-w-(--eg-control-touch) gap-(--eg-space-3) px-(--eg-space-2) py-(--eg-space-1) text-xs"
      [hlmDropdownMenuTrigger]="accountMenu"
      [align]="'end'"
      aria-label="Account menu"
      i18n-aria-label="@@shell.account.menu"
    >
      <span
        class="grid size-(--eg-control-md) shrink-0 place-items-center rounded-full bg-accent text-accent-foreground"
      >
        <app-shell-icon [name]="'account'" />
      </span>
      <span class="max-[1279px]:sr-only" i18n="@@shell.account.label">Account</span>
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
}
