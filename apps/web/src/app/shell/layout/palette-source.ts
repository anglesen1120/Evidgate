import { DOCUMENT } from '@angular/common';
import { Injectable, inject } from '@angular/core';
import { Router } from '@angular/router';
import { SessionStore } from '../../core/session/session.store';
import { NavViewService } from '../nav/nav-view';
import { localeBundlePath } from './locale-switcher.component';
import type { PaletteInput, PaletteLabels } from './palette-index';

/** Reads the shell's live state each time the palette computes; it never subscribes to routing. */
@Injectable({ providedIn: 'root' })
export class PaletteSource {
  private readonly nav = inject(NavViewService);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);

  snapshot(): PaletteInput {
    const base = this.document.baseURI;
    const route = this.router.url;
    const current = /(?:^|\/)vi\/$/.test(new URL(base).pathname) ? 'vi' : 'en';
    const otherLocale = current === 'en' ? 'vi' : 'en';
    // localeBundlePath flips its target, so passing the current locale yields the other bundle.
    const href = localeBundlePath(base, current, route);
    const localeHrefs: Partial<Record<'en' | 'vi', string>> = href ? { [otherLocale]: href } : {};
    return {
      destinations: this.nav.destinations(),
      orgId: this.session.currentOrg()?.id ?? null,
      projectId: this.session.currentProject()?.id ?? null,
      organizations: this.session.organizations(),
      projects: this.session.projects(),
      localeHrefs,
      labels: LABELS,
    };
  }
}

const LABELS: PaletteLabels = {
  navigate: $localize`:@@shell.palette.group-navigate:Navigate`,
  appearance: $localize`:@@shell.palette.group-appearance:Appearance`,
  language: $localize`:@@shell.palette.group-language:Language`,
  runs: $localize`:@@shell.palette.group-runs:Runs`,
  openRun: $localize`:@@shell.palette.open-run:Open run`,
  switchOrganization: (name) =>
    $localize`:@@shell.palette.switch-organization:Switch organization: ${name}:name:`,
  switchProject: (name) => $localize`:@@shell.palette.switch-project:Switch project: ${name}:name:`,
  theme: {
    light: $localize`:@@shell.palette.theme-light:Switch to light theme`,
    dark: $localize`:@@shell.palette.theme-dark:Switch to dark theme`,
    system: $localize`:@@shell.palette.theme-system:Switch to system theme`,
  },
  locale: {
    en: $localize`:@@shell.palette.locale-en:Switch to English`,
    vi: $localize`:@@shell.palette.locale-vi:Switch to Vietnamese`,
  },
};
