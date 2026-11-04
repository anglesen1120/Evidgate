import { CdkObserveContent } from '@angular/cdk/observers';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  contentChildren,
  type ElementRef,
  input,
  viewChild,
} from '@angular/core';
import { toObservable } from '@angular/core/rxjs-interop';
import { HlmIcon } from '@evidgate/ui/icon';
import {
  type BrnPaginatedTabHeaderItem,
  BrnTabsPaginatedList,
  BrnTabsTrigger,
} from '@spartan-ng/brain/tabs';
import { buttonVariants } from '@evidgate/ui/button';
import { classes, hlm } from '@evidgate/ui/utils';
import type { ClassValue } from 'clsx';
import type { Observable } from 'rxjs';
import { listVariants } from './hlm-tabs-list';

@Component({
  selector: 'hlm-paginated-tabs-list',
  exportAs: 'hlmTabsPaginatedList',
  imports: [CdkObserveContent, HlmIcon],

  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    'data-slot': 'tabs-paginated-list',
  },
  template: `
    <button
      #previousPaginator
      [attr.data-pagination]="'previous'"
      type="button"
      [attr.aria-hidden]="'true'"
      tabindex="-1"
      [class]="_paginationButtonClass()"
      [disabled]="disableScrollBefore || null"
      (click)="_handlePaginatorClick('before')"
      (mousedown)="_handlePaginatorPress('before', $event)"
      (touchend)="_stopInterval()"
    >
      <hlm-icon [name]="'lucideChevronLeft'" />
    </button>

    <div #tabListContainer class="z-[1] flex grow overflow-hidden">
      <div
        class="relative grow transition-transform"
        #tabList
        role="tablist"
        tabindex="-1"
        (keydown)="_handleKeydown($event)"
        (cdkObserveContent)="_onContentChanges()"
      >
        <div #tabListInner [class]="_tabListClass()">
          <ng-content />
        </div>
      </div>
    </div>

    <button
      #nextPaginator
      [attr.data-pagination]="'next'"
      type="button"
      [attr.aria-hidden]="'true'"
      tabindex="-1"
      [class]="_paginationButtonClass()"
      [disabled]="disableScrollAfter || null"
      (click)="_handlePaginatorClick('after')"
      (mousedown)="_handlePaginatorPress('after', $event)"
      (touchend)="_stopInterval()"
    >
      <hlm-icon [name]="'lucideChevronRight'" />
    </button>
  `,
})
export class HlmTabsPaginatedList extends BrnTabsPaginatedList {
  constructor() {
    super();
    classes(() => 'relative flex flex-shrink-0 items-center gap-1 overflow-hidden');
  }

  public readonly items = contentChildren(BrnTabsTrigger, { descendants: false });
  public readonly itemsChanges: Observable<ReadonlyArray<BrnPaginatedTabHeaderItem>> = toObservable(
    this.items,
  );

  public readonly tabListContainer =
    viewChild.required<ElementRef<HTMLElement>>('tabListContainer');
  public readonly tabList = viewChild.required<ElementRef<HTMLElement>>('tabList');
  public readonly tabListInner = viewChild.required<ElementRef<HTMLElement>>('tabListInner');
  public readonly nextPaginator = viewChild.required<ElementRef<HTMLElement>>('nextPaginator');
  public readonly previousPaginator =
    viewChild.required<ElementRef<HTMLElement>>('previousPaginator');

  public readonly tabListClass = input<ClassValue>('', { alias: 'tabListClass' });
  protected readonly _tabListClass = computed(() => hlm(listVariants(), this.tabListClass()));

  public readonly paginationButtonClass = input<ClassValue>('');
  protected readonly _paginationButtonClass = computed(() =>
    hlm(
      'relative z-[2] select-none disabled:cursor-default',
      buttonVariants({ variant: 'ghost', size: 'icon-sm' }),
      this.paginationButtonClass(),
      this.showPaginationControls() ? 'inline-flex' : 'hidden',
    ),
  );

  protected _itemSelected() {
    /* noop - the native button activation selects the focused tab */
  }
}
