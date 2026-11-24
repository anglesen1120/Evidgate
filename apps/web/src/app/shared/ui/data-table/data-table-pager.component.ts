import { formatNumber } from '@angular/common';
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
import { FormsModule, type NgModel } from '@angular/forms';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmLabel } from '@evidgate/ui/label';
import {
  HlmSelect,
  HlmSelectContent,
  HlmSelectItem,
  HlmSelectPortal,
  HlmSelectTrigger,
  HlmSelectValue,
} from '@evidgate/ui/select';
import { HlmTooltip } from '@evidgate/ui/tooltip';

let nextPagerId = 0;

@Component({
  selector: 'app-data-table-pager',
  imports: [
    FormsModule,
    HlmButton,
    HlmIcon,
    HlmLabel,
    HlmSelect,
    HlmSelectContent,
    HlmSelectItem,
    HlmSelectPortal,
    HlmSelectTrigger,
    HlmSelectValue,
    HlmTooltip,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './data-table-pager.component.html',
  styleUrl: './data-table-pager.component.css',
})
export class DataTablePagerComponent {
  private readonly locale = inject(LOCALE_ID);
  private readonly injector = inject(Injector);

  readonly page = input(1);
  readonly pageSize = input(20);
  readonly total = input<number | null>(null);
  readonly count = input(0);
  readonly hasNext = input(false);
  readonly busy = input(false);
  /** Retry cooldowns lock controls without claiming that a request is still pending. */
  readonly fetching = input<boolean | undefined>(undefined);
  protected readonly pending = computed(() => this.fetching() ?? this.busy());
  /** A loaded-row window has a fixed render cap, not a server page size or total. */
  readonly loadedCaption = input<string | null>(null);
  readonly previous = output<void>();
  readonly next = output<void>();
  readonly pageSizeChange = output<number>();

  protected readonly sizes = [20, 50, 100];
  protected readonly sizeId = `eg-table-pager-size-${nextPagerId++}`;
  protected readonly sizeLabel = $localize`:@@shared.data-table.rows-per-page:Rows per page`;
  protected readonly previousLabel = $localize`:@@shared.data-table.previous:Previous page`;
  protected readonly nextLabel = $localize`:@@shared.data-table.next:Next page`;
  protected readonly loadingLabel = $localize`:@@shared.states.loading.label:Loading…`;
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
  protected readonly pageLabel = computed(() => {
    const page = formatNumber(this.page(), this.locale);
    return $localize`:@@shared.data-table.page-number:Page ${page}:page:`;
  });
  protected readonly summary = computed(() => {
    const total = this.total();
    // A cursor can follow a short page, so its global ordinal is unknown.
    const offset = total === null ? 0 : (this.page() - 1) * this.pageSize();
    const count = this.count();
    const shown = total === null ? count : Math.min(count, total - offset);
    const start = formatNumber(shown > 0 ? offset + 1 : 0, this.locale);
    const end = formatNumber(shown > 0 ? offset + shown : 0, this.locale);
    if (total === null) {
      return $localize`:@@shared.data-table.cursor-range:Showing ${start}:start:–${end}:end:`;
    }
    const loaded = formatNumber(total, this.locale);
    return this.loadedCaption() === null
      ? $localize`:@@shared.data-table.range:Showing ${start}:start:–${end}:end: of ${loaded}:loaded:`
      : $localize`:@@shared.data-table.showing-loaded-range:Showing ${start}:start:–${end}:end: of ${loaded}:loaded: loaded records`;
  });
  private awaitingRender = false;

  protected move(forward: boolean): void {
    if ((forward ? !this.canNext() : this.page() <= 1) || !this.beginIntent()) return;
    (forward ? this.next : this.previous).emit();
  }

  protected chooseSize(value: unknown, field: NgModel): void {
    const served = this.pageSize();
    if (value === served) return;
    // Helm has already selected the requested size; only the owner can commit it.
    field.control.setValue(served);
    if (typeof value === 'number' && this.sizes.includes(value) && this.beginIntent()) {
      this.pageSizeChange.emit(value);
    }
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
