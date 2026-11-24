import { DOCUMENT, formatNumber } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  Injector,
  input,
  LOCALE_ID,
  output,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmLabel } from '@evidgate/ui/label';
import {
  HlmSelect,
  HlmSelectContent,
  HlmSelectItem,
  HlmSelectPortal,
  HlmSelectTrigger,
  HlmSelectValue,
} from '@evidgate/ui/select';

let nextPagerId = 0;

@Component({
  selector: 'app-data-table-pager',
  imports: [
    HlmButton,
    HlmLabel,
    HlmSelect,
    HlmSelectContent,
    HlmSelectItem,
    HlmSelectPortal,
    HlmSelectTrigger,
    HlmSelectValue,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './data-table-pager.component.html',
  styleUrl: './data-table-pager.component.css',
})
export class DataTablePagerComponent {
  private readonly locale = inject(LOCALE_ID);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);

  readonly page = input(1);
  readonly pageSize = input(20);
  readonly total = input<number | null>(null);
  readonly count = input(0);
  readonly hasNext = input(false);
  readonly busy = input(false);
  /** A loaded-row window has a fixed render cap, not a server page size or total. */
  readonly loadedCaption = input<string | null>(null);
  readonly previous = output<void>();
  readonly next = output<void>();
  readonly pageSizeChange = output<number>();

  protected readonly sizes = [20, 50, 100];
  protected readonly sizeId = `eg-table-pager-size-${nextPagerId++}`;
  protected readonly sizeLabel = $localize`:@@shared.data-table.rows-per-page:Rows per page`;
  protected readonly previousLabel = $localize`:@@shared.data-table.previous:Previous records`;
  protected readonly nextLabel = $localize`:@@shared.data-table.next:Next records`;
  protected readonly landmark = computed(() => {
    const caption = this.loadedCaption();
    return caption === null
      ? $localize`:@@shared.data-table.pagination:Table pagination`
      : $localize`:@@shared.data-table.pages:${caption}:caption: — loaded record pages`;
  });
  protected readonly canNext = computed(() => {
    const total = this.total();
    return this.hasNext() && (total === null || this.page() * this.pageSize() < total);
  });
  protected readonly summary = computed(() => {
    const total = this.total();
    if (total === null) {
      const page = formatNumber(this.page(), this.locale);
      return $localize`:@@shared.data-table.page-number:Page ${page}:page:`;
    }
    const offset = (this.page() - 1) * this.pageSize();
    const hasRows = this.count() > 0 && offset < total;
    const start = formatNumber(hasRows ? offset + 1 : 0, this.locale);
    const end = formatNumber(hasRows ? Math.min(total, offset + this.count()) : 0, this.locale);
    const loaded = formatNumber(total, this.locale);
    return this.loadedCaption() === null
      ? $localize`:@@shared.data-table.range:${start}:start:–${end}:end: of ${loaded}:loaded:`
      : $localize`:@@shared.data-table.loaded-range:Loaded records ${start}:start:–${end}:end: of ${loaded}:loaded:`;
  });
  private awaitingRender = false;

  protected move(forward: boolean, source: HTMLButtonElement, opposite: HTMLButtonElement): void {
    if ((forward ? !this.canNext() : this.page() <= 1) || !this.beginIntent()) return;
    const owned = this.document.activeElement === source;
    (forward ? this.next : this.previous).emit();
    if (owned && this.loadedCaption() !== null) {
      afterNextRender(
        () => {
          if (!source.disabled || !opposite.isConnected || opposite.disabled) return;
          const active = this.document.activeElement;
          if (active === source || active === this.document.body || !active?.isConnected) {
            opposite.focus();
          }
        },
        { injector: this.injector },
      );
    }
  }

  protected chooseSize(value: unknown): void {
    if (typeof value !== 'number' || !this.sizes.includes(value) || value === this.pageSize()) {
      return;
    }
    if (this.beginIntent()) this.pageSizeChange.emit(value);
  }

  private beginIntent(): boolean {
    if (this.busy() || this.awaitingRender) return false;
    // Cover synchronous repeated activation before the owner's busy input reaches this view.
    this.awaitingRender = true;
    afterNextRender(
      () => {
        this.awaitingRender = false;
      },
      { injector: this.injector },
    );
    return true;
  }
}
