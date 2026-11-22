import {
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  output,
  TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';
import {
  DataTableComponent,
  type DataTableCellContext,
  type DataTableColumn,
} from '../../../shared/ui/data-table';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { galleryCopy } from './studio-gallery.copy';
import type { GalleryTemplateViewModel as Template } from './studio-gallery.fixture';

type Cell = TemplateRef<DataTableCellContext<Template>>;

@Component({
  selector: 'app-gallery-template-table',
  imports: [DataTableComponent, HlmBadge, HlmButton, HlmIcon, ShellIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 [&_tr[data-row-id]]:h-(--eg-space-11)' },
  template: `
    <app-data-table
      [caption]="copy.title"
      [columns]="columns()"
      [rows]="rows()"
      [rowId]="rowId"
      [flow]="'page'"
      [fitToContainer]="true"
    />
    <ng-template #subject let-item>
      <div class="flex min-w-0 items-start gap-(--eg-space-3)">
        <span
          class="grid size-(--eg-control-sm) shrink-0 place-items-center rounded-(--eg-radius-sm) bg-muted text-muted-foreground"
          ><app-shell-icon [name]="'file'"
        /></span>
        <div class="min-w-0">
          <strong class="block text-sm font-semibold wrap-anywhere"
            ><bdi>{{ item.title }}</bdi></strong
          >
          <span class="inline-flex items-center gap-1 text-xs text-muted-foreground">
            <code
              ><bdi>{{ item.id }}</bdi></code
            >
            @if (item.id === selected()) {
              <span data-template-selected class="inline-flex items-center gap-1">
                <hlm-icon name="lucideCheck" class="size-4" />{{ copy.selected }}
              </span>
            }
          </span>
        </div>
      </div>
    </ng-template>
    <ng-template #engine let-item>
      <span class="block">{{ item.engine }}</span>
      <code class="text-xs text-muted-foreground"
        ><bdi>{{ item.variant }}</bdi></code
      >
    </ng-template>
    <ng-template #source let-item>
      <span hlmBadge [variant]="'secondary'" data-template-source>
        <hlm-icon name="lucideInfo" />{{
          item.source === 'built' ? copy.builtBadge : copy.teamBadge
        }}
      </span>
    </ng-template>
    <ng-template #actions let-item>
      <button
        #trigger
        hlmBtn
        type="button"
        [variant]="'outline'"
        [size]="'sm'"
        class="shrink-0 whitespace-nowrap"
        [attr.data-gallery-view]="item.id"
        [attr.aria-haspopup]="'dialog'"
        (click)="choose(item, trigger)"
      >
        {{ copy.view
        }}<span class="sr-only"
          >&ngsp;<bdi>{{ item.title }}</bdi></span
        >
      </button>
    </ng-template>
  `,
})
export class GalleryTemplateTableComponent {
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  private returnIndex = 0;
  readonly rows = input.required<readonly Template[]>();
  readonly selected = input<Template['id']>();
  readonly picked = output<Template['id']>();
  private readonly subject = viewChild<Cell>('subject');
  private readonly engine = viewChild<Cell>('engine');
  private readonly source = viewChild<Cell>('source');
  private readonly actions = viewChild<Cell>('actions');
  protected readonly copy = galleryCopy;
  protected readonly rowId = (item: Template) => item.id;
  protected readonly columns = computed<readonly DataTableColumn<Template>[]>(() => {
    const subject = this.subject(),
      engine = this.engine(),
      source = this.source(),
      actions = this.actions();
    if (!subject || !engine || !source || !actions) return [];
    return [
      {
        id: 'title',
        role: 'identity',
        header: this.copy.template,
        cell: (item) => item.title,
        cellTemplate: subject,
        overflow: 'wrap',
      },
      {
        id: 'engine',
        header: this.copy.engineVariant,
        cell: (item) => item.engine,
        cellTemplate: engine,
      },
      { id: 'source', header: this.copy.source, cell: (item) => item.source, cellTemplate: source },
      { id: 'assertions', header: this.copy.assertions, cell: (item) => item.assertion },
      {
        id: 'actions',
        role: 'actions',
        width: { min: 'max-content', preferred: '1%' },
        header: this.copy.actions,
        cell: () => this.copy.view,
        cellTemplate: actions,
      },
    ];
  });

  protected choose(item: Template, trigger: HTMLButtonElement): void {
    this.returnIndex = Math.max(
      0,
      this.rows().findIndex((row) => row.id === item.id),
    );
    trigger.focus();
    this.picked.emit(item.id);
  }

  focusView(id: Template['id']): boolean {
    const root = this.host.nativeElement;
    const buttons = Array.from(root.querySelectorAll<HTMLButtonElement>('[data-gallery-view]'));
    const button =
      buttons.find((candidate) => candidate.dataset['galleryView'] === id) ??
      buttons[Math.min(this.returnIndex, buttons.length - 1)];
    button?.focus();
    return !!button;
  }
}
