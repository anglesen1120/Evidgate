import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  afterNextRender,
  afterRenderEffect,
  computed,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { DOCUMENT } from '@angular/common';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { BrnDialogRef } from '@spartan-ng/brain/dialog';
import { type PaletteAction, type PaletteItem, buildPaletteSections } from './palette-index';
import { PaletteSource } from './palette-source';

const OPTION_PREFIX = 'palette-option-';

@Component({
  selector: 'app-shell-palette',
  imports: [HlmDialogImports, HlmInput, HlmLabel],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './shell-palette.component.html',
})
export class ShellPaletteComponent {
  private readonly dialog = inject<BrnDialogRef<PaletteAction | undefined>>(BrnDialogRef);
  private readonly document = inject(DOCUMENT);
  private readonly source = inject(PaletteSource);
  private readonly field = viewChild.required<ElementRef<HTMLInputElement>>('field');

  readonly query = signal('');
  // Reads the nav and session signals inside a computed so a permission change updates open results.
  readonly sections = computed(() => buildPaletteSections(this.source.snapshot(), this.query()));
  readonly flat = computed(() => this.sections().flatMap((section) => section.items));
  private readonly positions = computed(
    () => new Map(this.flat().map((item, index) => [item.id, index])),
  );
  protected readonly title = $localize`:@@shell.palette.title:Command palette`;
  protected readonly fieldLabel = $localize`:@@shell.palette.field:Find a command`;
  protected readonly matchesLabel = $localize`:@@shell.palette.matches:Matching commands`;
  protected readonly footerHint = $localize`:@@shell.palette.footer:↑↓ navigate · Enter execute · Esc close`;
  // A new result list resets the highlight so a stale index never points at another command.
  readonly active = linkedSignal({ source: this.flat, computation: () => -1 });
  protected readonly activeId = computed(() => {
    const item = this.flat()[this.active()];
    return item ? `${OPTION_PREFIX}${item.id}` : null;
  });
  protected readonly statusText = computed(() => {
    const count = this.flat().length;
    if (count === 0) return $localize`:@@shell.palette.empty:No results`;
    return count === 1
      ? $localize`:@@shell.palette.count-one:1 result`
      : $localize`:@@shell.palette.count-many:${count}:count: results`;
  });

  constructor() {
    afterNextRender(() => this.field().nativeElement.focus());
    afterRenderEffect(() => {
      const id = this.activeId();
      if (id) this.document.getElementById(id)?.scrollIntoView?.({ block: 'nearest' });
    });
  }

  protected optionId(item: PaletteItem): string {
    return `${OPTION_PREFIX}${item.id}`;
  }

  protected isActive(item: PaletteItem): boolean {
    return this.positions().get(item.id) === this.active();
  }

  protected onInput(event: Event): void {
    this.query.set((event.target as HTMLInputElement).value);
  }

  protected onKeydown(event: KeyboardEvent): void {
    if (event.key === 'ArrowDown') this.move(event, 1);
    else if (event.key === 'ArrowUp') this.move(event, -1);
    else if (event.key === 'Enter') {
      event.preventDefault();
      this.choose(this.flat()[Math.max(0, this.active())]);
    }
  }

  protected choose(item: PaletteItem | undefined): void {
    if (item) this.dialog.close(item.action);
  }

  private move(event: Event, direction: 1 | -1): void {
    event.preventDefault();
    const count = this.flat().length;
    if (count === 0) return;
    this.active.update((index) =>
      index < 0 ? (direction === 1 ? 0 : count - 1) : (index + direction + count) % count,
    );
  }
}
