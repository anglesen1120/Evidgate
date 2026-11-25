import {
  ChangeDetectionStrategy,
  Component,
  LOCALE_ID,
  inject,
  input,
  output,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import {
  HlmDropdownMenu,
  HlmDropdownMenuGroup,
  HlmDropdownMenuItem,
  HlmDropdownMenuLabel,
  HlmDropdownMenuRadio,
  HlmDropdownMenuRadioIndicator,
  HlmDropdownMenuSeparator,
  HlmDropdownMenuTrigger,
} from '@evidgate/ui/dropdown-menu';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { SessionStore } from '../../core/session/session.store';
import { ShellIconComponent } from './shell-icon.component';
import { ShellAccountNameComponent } from './shell-account-name.component';
import { ThemeService } from './theme.service';

@Component({
  selector: 'app-shell-account',
  imports: [
    HlmButton,
    HlmDropdownMenu,
    HlmDropdownMenuGroup,
    HlmDropdownMenuItem,
    HlmDropdownMenuLabel,
    HlmDropdownMenuRadio,
    HlmDropdownMenuRadioIndicator,
    HlmDropdownMenuSeparator,
    HlmDropdownMenuTrigger,
    HlmTooltip,
    ShellAccountNameComponent,
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
      [attr.aria-label]="accountLabel"
    >
      <span
        class="grid size-(--eg-control-sm) shrink-0 place-items-center rounded-full border-(length:--eg-border-width) border-solid border-primary bg-accent text-accent-foreground"
      >
        <app-shell-icon [name]="'account'" />
      </span>
    </button>
    <ng-template #accountMenu>
      <hlm-dropdown-menu class="w-[calc(4*var(--eg-space-12))]" [attr.aria-label]="accountLabel">
        <app-shell-account-name />
        @if (canSwitchLocale()) {
          <div hlmDropdownMenuGroup aria-labelledby="account-language">
            <div hlmDropdownMenuLabel id="account-language" i18n="@@catalog.case-view.language">
              Language
            </div>
            @for (choice of languages; track choice.code) {
              <button
                type="button"
                hlmDropdownMenuRadio
                [attr.lang]="choice.code"
                [checked]="locale === choice.code"
                (triggered)="chooseLocale(choice.code)"
              >
                {{ choice.label }}
                <hlm-dropdown-menu-radio-indicator />
              </button>
            }
          </div>
          <hlm-dropdown-menu-separator />
        }
        <div hlmDropdownMenuGroup aria-labelledby="account-theme">
          <div hlmDropdownMenuLabel id="account-theme" i18n="@@shell.account.theme">Theme</div>
          @for (option of themes; track option.value) {
            <button
              type="button"
              hlmDropdownMenuRadio
              [checked]="theme.choice() === option.value"
              (triggered)="theme.choose(option.value)"
            >
              {{ option.label }}
              <hlm-dropdown-menu-radio-indicator />
            </button>
          }
        </div>
        <hlm-dropdown-menu-separator />
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
  protected readonly theme = inject(ThemeService);
  protected readonly locale = inject(LOCALE_ID).startsWith('vi') ? 'vi' : 'en';
  readonly canSwitchLocale = input(false);
  readonly switchRequested = output<void>();
  protected readonly accountLabel = $localize`:@@shell.account.preferences:Account and preferences`;
  protected readonly languages = [
    { code: 'en', label: $localize`:@@auth.login.locale-en:EN` },
    { code: 'vi', label: $localize`:@@auth.login.locale-vi:VI` },
  ] as const;
  protected readonly themes = [
    { value: 'light', label: $localize`:@@shell.theme.light:Light` },
    { value: 'dark', label: $localize`:@@shell.theme.dark:Dark` },
    { value: 'system', label: $localize`:@@shell.theme.system:System` },
  ] as const;

  protected chooseLocale(locale: 'en' | 'vi'): void {
    if (locale !== this.locale) this.switchRequested.emit();
  }
}
