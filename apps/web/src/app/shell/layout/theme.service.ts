import { DOCUMENT } from '@angular/common';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';

type ThemeChoice = 'light' | 'dark' | 'system';

/** An explicit choice overrides the OS; absence deliberately leaves data-theme unset. */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly preference = signal<ThemeChoice>('system');
  readonly choice = this.preference.asReadonly();
  private readonly systemDark = signal(false);
  readonly isDark = computed(
    () => this.choice() === 'dark' || (this.choice() === 'system' && this.systemDark()),
  );

  constructor() {
    const window = this.document.defaultView;
    const media = window?.matchMedia?.('(prefers-color-scheme: dark)');
    this.systemDark.set(media?.matches ?? false);
    const onChange = (event: MediaQueryListEvent) => this.systemDark.set(event.matches);
    media?.addEventListener('change', onChange);
    inject(DestroyRef).onDestroy(() => media?.removeEventListener('change', onChange));
    try {
      const stored = window?.localStorage.getItem('evidgate-theme');
      if (stored === 'light' || stored === 'dark') this.preference.set(stored);
    } catch {
      // Storage can be denied; the accessible theme control still works for this visit.
    }
    this.apply();
  }

  toggle(): void {
    this.choose(this.isDark() ? 'light' : 'dark');
  }

  choose(choice: ThemeChoice): void {
    this.preference.set(choice);
    this.apply();
    try {
      const storage = this.document.defaultView?.localStorage;
      if (choice === 'system') storage?.removeItem('evidgate-theme');
      else storage?.setItem('evidgate-theme', choice);
    } catch {
      // Persistence is optional, never a prerequisite to changing the visible theme.
    }
  }

  private apply(): void {
    const choice = this.preference();
    if (choice === 'system') this.document.documentElement.removeAttribute('data-theme');
    else this.document.documentElement.setAttribute('data-theme', choice);
  }
}
