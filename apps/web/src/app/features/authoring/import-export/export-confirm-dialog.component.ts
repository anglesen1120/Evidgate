import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { IMPEX_COPY } from './import-export.copy';

@Component({
  selector: 'app-authoring-export-confirm',
  imports: [HlmButton, HlmDialogImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [autoFocus]="'[data-export-cancel]'"
      [restoreFocus]="true"
      [closeOnOutsidePointerEvents]="false"
      [aria-label]="copy.confirmDownload"
      (stateChanged)="opened.set($event === 'open')"
      (closed)="finish()"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ copy.confirmDownload }}</h2>
        </header>
        <p hlmDialogDescription>{{ scope() }}</p>
        <dl class="grid min-w-0 gap-(--eg-space-3)">
          <div>
            <dt>{{ copy.format }}</dt>
            <dd class="m-0">{{ format() }}</dd>
          </div>
          <div>
            <dt>{{ copy.include }}</dt>
            <dd class="m-0">{{ included() }}</dd>
          </div>
        </dl>
        <p class="m-0 text-sm text-muted-foreground">{{ copy.countUnavailable }}</p>
        <footer hlmDialogFooter>
          <button
            hlmBtn
            [variant]="'outline'"
            type="button"
            data-export-cancel
            class="min-h-(--eg-control-touch)"
            (click)="opened.set(false)"
          >
            {{ copy.cancel }}
          </button>
          <button
            hlmBtn
            type="button"
            data-export-confirm
            class="min-h-(--eg-control-touch) whitespace-normal"
            (click)="accept()"
          >
            {{ copy.download }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class ExportConfirmDialogComponent {
  readonly scope = input.required<string>();
  readonly format = input.required<string>();
  readonly included = input.required<string>();
  readonly confirmed = output<void>();
  readonly dismissed = output<void>();
  protected readonly copy = IMPEX_COPY;
  protected readonly opened = signal(true);
  private accepted = false;
  private finished = false;
  protected accept(): void {
    this.accepted = true;
    this.opened.set(false);
  }
  protected finish(): void {
    if (this.finished) return;
    this.finished = true;
    if (this.accepted) this.confirmed.emit();
    else this.dismissed.emit();
  }
}
