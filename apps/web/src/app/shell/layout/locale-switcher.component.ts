import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { ShellIconComponent } from './shell-icon.component';

/** Localized bundles are siblings; retain any deployment prefix and the entire route suffix. */
export function localeBundlePath(baseURI: string, locale: string, route: string): string | null {
  const base = new URL(baseURI).pathname;
  const segment = /(?:^|\/)(?:en|vi)\/$/.exec(base);
  if (segment === null) return null;
  const target = locale.startsWith('vi') ? 'en' : 'vi';
  const path = route.startsWith('/') ? route : `/${route}`;
  return `${base.slice(0, segment.index)}/${target}${path}`;
}

@Component({
  selector: 'app-locale-switcher',
  imports: [HlmButton, ShellIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  template: `
    <button
      type="button"
      hlmBtn
      [variant]="variant()"
      [size]="'sm'"
      class="locale-toggle min-h-(--eg-control-touch) min-w-(--eg-control-touch) gap-(--eg-space-2)"
      [disabled]="disabled()"
      [attr.aria-label]="localeLabel"
      (click)="requestSwitch()"
    >
      <app-shell-icon [name]="'globe'" />
      <span lang="en" i18n="@@auth.login.locale-en">EN</span>
      <span aria-hidden="true"> / </span>
      <span lang="vi" i18n="@@auth.login.locale-vi">VI</span>
    </button>
  `,
})
export class LocaleSwitcherComponent {
  readonly variant = input<'outline' | 'ghost'>('outline');
  readonly disabled = input(false);
  readonly switchRequested = output<void>();
  readonly localeLabel = $localize`:@@auth.login.switch-language:EN / VI: Switch language between English and Vietnamese`;

  requestSwitch(): void {
    if (!this.disabled()) this.switchRequested.emit();
  }
}
