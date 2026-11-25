import { DOCUMENT } from '@angular/common';
import { ChangeDetectionStrategy, Component, Injector, inject } from '@angular/core';
import { Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogService } from '@evidgate/ui/dialog';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { ShellIconComponent } from './shell-icon.component';
import type { PaletteAction } from './palette-index';
import { ThemeService } from './theme.service';

const SHORTCUT_KEY = 'k';

/** Opens the command palette from the topbar trigger or Cmd/Ctrl+K. */
@Component({
  selector: 'app-shell-search',
  imports: [HlmButton, HlmTooltip, ShellIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    '(document:keydown)': 'onShortcut($event)',
  },
  templateUrl: './shell-search.component.html',
})
export class ShellSearchComponent {
  private readonly dialogs = inject(HlmDialogService);
  private readonly router = inject(Router);
  private readonly theme = inject(ThemeService);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private loading = false;

  protected readonly shortcut = /Mac|iPhone|iPad|iPod/.test(
    this.document.defaultView?.navigator.userAgent ?? '',
  )
    ? '⌘K'
    : 'Ctrl K';
  protected readonly label = $localize`:@@shell.palette.trigger:Command palette (${this.shortcut}:shortcut:)`;

  protected async open(): Promise<void> {
    // The palette chunk loads on first use; the flag stops a second open during that load.
    if (this.loading || this.document.querySelector('[role="dialog"]')) return;
    this.loading = true;
    try {
      const { ShellPaletteComponent } = await import('./shell-palette.component');
      const ref = this.dialogs.open<PaletteAction | undefined>(ShellPaletteComponent, {
        contentClass: 'shell-palette',
        size: 'md',
      });
      ref.closed$.subscribe((action) => this.execute(action));
    } finally {
      this.loading = false;
    }
  }

  protected onShortcut(event: KeyboardEvent): void {
    if (event.key.toLowerCase() !== SHORTCUT_KEY || event.altKey || event.shiftKey) return;
    if (!event.metaKey && !event.ctrlKey) return;
    if (isEditable(event.target)) return;
    event.preventDefault();
    void this.open();
  }

  // Runs after the dialog restores focus, so the target page receives focus from the shell.
  private execute(action: PaletteAction | undefined): void {
    if (!action) return;
    if (action.kind === 'navigate') void this.router.navigate(action.link);
    else if (action.kind === 'theme') this.theme.choose(action.choice);
    else this.document.location.assign(action.href);
  }
}

function isEditable(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName);
}
