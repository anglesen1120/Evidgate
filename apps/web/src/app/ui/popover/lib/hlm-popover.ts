import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  TemplateRef,
  contentChild,
  input,
  viewChild,
} from '@angular/core';
import { BrnPopover, BrnPopoverImports } from '@spartan-ng/brain/popover';
import { HlmButton } from '@evidgate/ui/button';

@Component({
  selector: 'hlm-popover',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [BrnPopoverImports, HlmButton, NgTemplateOutlet],
  template: `
    <brn-popover
      [id]="panelId()"
      [align]="'end'"
      [sideOffset]="4"
      [role]="null"
      [aria-modal]="false"
    >
      <button
        #trigger
        hlmBtn
        brnPopoverTrigger
        type="button"
        [variant]="'outline'"
        [size]="'sm'"
        [disabled]="disabled()"
        class="max-md:min-h-(--eg-control-touch) pointer-coarse:min-h-(--eg-control-touch)"
        (click)="trigger.focus()"
      >
        {{ label() }}
      </button>
      <div
        *brnPopoverContent
        role="dialog"
        [attr.aria-label]="label()"
        class="grid w-72 max-w-[calc(100vw-2rem)] gap-3 rounded-(--eg-radius-md) border border-border bg-popover p-4 text-popover-foreground shadow-(--eg-elevation-overlay)"
      >
        <ng-container [ngTemplateOutlet]="content()" />
      </div>
    </brn-popover>
  `,
})
export class HlmPopover {
  readonly panelId = input.required<string>();
  readonly label = input.required<string>();
  readonly disabled = input(false);
  protected readonly content = contentChild.required(TemplateRef);
  private readonly popover = viewChild.required(BrnPopover);
  private readonly trigger = viewChild.required<ElementRef<HTMLButtonElement>>('trigger');
  close(): void {
    this.popover().close();
  }
  focusTrigger(): void {
    this.trigger().nativeElement.focus();
  }
}
