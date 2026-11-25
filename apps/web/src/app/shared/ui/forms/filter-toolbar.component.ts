import { DecimalPipe, NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  TemplateRef,
  contentChild,
  input,
  output,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { HlmButton } from '@evidgate/ui/button';
import { HlmPopover } from '@evidgate/ui/popover';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';

@Component({
  selector: 'app-filter-toolbar',
  imports: [DecimalPipe, FormsModule, HlmButton, HlmPopover, NgTemplateOutlet, ShellIconComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'flex min-w-0 flex-wrap items-center gap-2 border-b border-border px-(--eg-space-5) py-2 md:px-(--eg-space-6)',
  },
  template: `
    <ng-content select="[filterChips]" />
    <form
      class="flex min-w-0 flex-[1_1_8rem] items-center gap-1 md:ms-auto md:max-w-80"
      [attr.aria-label]="applyLabel()"
      (ngSubmit)="!busy() && submitted.emit()"
    >
      <ng-content select="[filterSearch]" />
      <button
        hlmBtn
        [size]="'icon-sm'"
        [variant]="'ghost'"
        type="submit"
        data-filter-search
        class="max-md:size-(--eg-control-touch) pointer-coarse:size-(--eg-control-touch)"
        [attr.aria-label]="applyLabel()"
        [attr.aria-busy]="busy()"
        [attr.aria-disabled]="busy()"
      >
        <app-shell-icon class="size-4" [name]="'search'" />
      </button>
    </form>
    <hlm-popover
      #options
      [panelId]="panelId()"
      [label]="filtersLabel() + ' (' + (count() | number) + ')'"
    >
      <ng-template>
        <form
          class="grid min-w-0 gap-2"
          [attr.aria-label]="filtersLabel()"
          (ngSubmit)="!busy() && apply.emit()"
        >
          <ng-container [ngTemplateOutlet]="secondary()" />
          <button
            hlmBtn
            [size]="'sm'"
            type="submit"
            data-filter-apply
            class="justify-self-end max-md:min-h-(--eg-control-touch) pointer-coarse:min-h-(--eg-control-touch)"
            [attr.aria-busy]="busy()"
            [attr.aria-disabled]="busy()"
          >
            {{ applyLabel() }}
          </button>
        </form>
      </ng-template>
    </hlm-popover>
    @if (active()) {
      <button
        hlmBtn
        type="button"
        [variant]="'ghost'"
        [size]="'sm'"
        data-filter-clear
        class="max-md:min-h-(--eg-control-touch) pointer-coarse:min-h-(--eg-control-touch)"
        [attr.aria-busy]="busy()"
        [attr.aria-disabled]="busy()"
        (click)="!busy() && clearFromToolbar()"
      >
        {{ clearLabel() }}
      </button>
    }
  `,
})
export class FilterToolbarComponent {
  readonly panelId = input.required<string>();
  readonly count = input.required<number>();
  readonly active = input.required<boolean>();
  readonly busy = input.required<boolean>();
  readonly applyLabel = input.required<string>();
  readonly clearLabel = input.required<string>();
  readonly filtersLabel = input.required<string>();
  readonly submitted = output<void>();
  readonly apply = output<void>();
  readonly clear = output<void>();
  protected readonly secondary = contentChild.required<TemplateRef<unknown>>('filterOptions');
  private readonly options = viewChild.required(HlmPopover);
  close(): void {
    this.options().close();
  }
  protected clearFromToolbar(): void {
    // Clear disappears after applying; return focus before its button leaves the document.
    this.options().focusTrigger();
    this.clear.emit();
  }
}
