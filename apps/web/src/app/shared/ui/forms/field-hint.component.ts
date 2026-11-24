import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  Renderer2,
  signal,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';
import { openFullValuePopover } from '@evidgate/ui/utils';

@Component({
  selector: 'app-field-hint',
  imports: [HlmButton, HlmIcon],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: '../../../ui/utils/lib/full-value-popover.css',
  host: {
    class: 'inline-flex min-w-0 items-center gap-(--eg-space-2) pointer-coarse:gap-(--eg-space-3)',
  },
  template: `
    <ng-content />
    @if (text(); as hint) {
      <button
        #trigger
        hlmBtn
        type="button"
        variant="ghost"
        size="icon-sm"
        class="shrink-0 text-muted-foreground pointer-coarse:size-(--eg-control-touch)"
        [attr.aria-label]="name()"
        [attr.aria-controls]="descriptionId()"
        [attr.aria-describedby]="descriptionId()"
        [attr.aria-expanded]="expanded()"
        (click)="toggle(trigger, panel)"
        (keydown.escape)="escape($event)"
      >
        <hlm-icon name="lucideInfo" />
      </button>
      <span
        #panel
        popover="manual"
        class="box-border w-max overflow-auto rounded-(--eg-radius-md) border border-border bg-popover p-(--eg-space-4) text-start text-sm font-normal text-popover-foreground whitespace-normal wrap-anywhere shadow-md"
        [id]="descriptionId()"
        ><span class="block max-w-prose">{{ hint }}</span></span
      >
    }
  `,
})
export class FieldHintComponent {
  private readonly renderer = inject(Renderer2);
  private readonly destroyRef = inject(DestroyRef);
  readonly label = input.required<string>();
  readonly text = input.required<string | undefined>();
  readonly descriptionId = input.required<string>();
  protected readonly expanded = signal(false);
  protected readonly name = computed(
    () => $localize`:@@shared.field-hint.name:Hint: ${this.label()}:label:`,
  );
  private dismiss: (() => void) | undefined;

  constructor() {
    effect(() => {
      if (!this.text()) this.dismiss?.();
    });
    this.destroyRef.onDestroy(() => this.dismiss?.());
  }

  protected toggle(trigger: HTMLElement, panel: HTMLElement): void {
    if (this.dismiss) {
      this.dismiss();
      return;
    }
    const close = openFullValuePopover(trigger, panel, () => this.dismiss?.());
    const unlisten = this.renderer.listen(
      trigger.ownerDocument,
      'click',
      (event: MouseEvent) => {
        const path = event.composedPath();
        if (!path.includes(trigger) && !path.includes(panel)) this.dismiss?.();
      },
      { capture: true },
    );
    this.expanded.set(true);
    this.dismiss = () => {
      unlisten();
      close();
      this.expanded.set(false);
      this.dismiss = undefined;
    };
  }

  protected escape(event: Event): void {
    if (!this.dismiss) return;
    event.stopPropagation();
    this.dismiss();
  }
}
