import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  afterRenderEffect,
  computed,
  inject,
  linkedSignal,
  signal,
} from '@angular/core';
import { Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { NavViewService } from '../nav/nav-view';
import { ShellIconComponent } from './shell-icon.component';

@Component({
  selector: 'app-shell-search',
  imports: [HlmButton, HlmDialogImports, HlmInput, HlmLabel, HlmTooltip, ShellIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell-search.component.html',
})
export class ShellSearchComponent {
  private readonly nav = inject(NavViewService);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  readonly searchLabel = $localize`:@@shell.search.trigger:Search`;
  readonly query = signal('');
  readonly results = computed(() => {
    const term = this.query().trim().toLowerCase();
    return this.nav
      .groups()
      .flatMap((group) => group.items.filter((item) => item.label.toLowerCase().includes(term)));
  });
  readonly active = linkedSignal({ source: this.results, computation: () => -1 });
  readonly activeId = computed(() => {
    const item = this.results()[this.active()];
    return item ? `screen-option-${item.id}` : null;
  });

  constructor() {
    afterRenderEffect(() => {
      const id = this.activeId();
      if (id) this.document.getElementById(id)?.scrollIntoView?.({ block: 'nearest' });
    });
  }

  move(event: Event, direction: 1 | -1): void {
    event.preventDefault();
    const count = this.results().length;
    if (count === 0) return;
    this.active.update((index) =>
      index < 0 ? (direction === 1 ? 0 : count - 1) : (index + direction + count) % count,
    );
  }

  // Only navigate after the dialog has restored focus; the shell then focuses the new page.
  navigate(selected: unknown): void {
    const item = this.results().find((result) => result === selected);
    this.query.set('');
    this.active.set(-1);
    if (item) void this.router.navigate(item.link);
  }

  choose(event: Event, picker: HlmDialog): void {
    event.preventDefault();
    const item = this.results()[Math.max(0, this.active())];
    if (item) picker.close(item);
  }
}
