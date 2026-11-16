import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { SECRETS_COPY } from './secrets.copy';

@Component({
  selector: 'app-rotate-confirm-dialog',
  imports: [HlmButton, HlmDialogImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [role]="'alertdialog'"
      [autoFocus]="'[data-rotation-cancel]'"
      [restoreFocus]="false"
      [closeOnOutsidePointerEvents]="false"
      [aria-label]="title"
      (stateChanged)="stateChanged($event)"
      (closed)="finish()"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere"
        data-rotation-confirm
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ title }}</h2>
        </header>
        <p hlmDialogDescription>{{ warning }}</p>
        <dl
          class="m-0 grid min-w-0 gap-[var(--eg-space-4)] [&_dd]:m-0 [&_dd]:wrap-anywhere [&_dt]:text-muted-foreground"
        >
          <div>
            <dt>{{ referenceLabel }}</dt>
            <dd>{{ reference().name }}</dd>
          </div>
          <div>
            <dt>{{ copy.project }}</dt>
            <dd>{{ projectName() }}</dd>
          </div>
          <div>
            <dt>{{ copy.prefix }}</dt>
            <dd>
              <code>{{ reference().prefix }}</code>
            </dd>
          </div>
          <div>
            <dt>{{ copy.scope }}</dt>
            <dd>
              @for (scope of reference().scopes; track scope) {
                <code class="block">{{ scope }}</code>
              } @empty {
                {{ copy.unavailable }}
              }
            </dd>
          </div>
        </dl>
        <p class="m-0">{{ revealWarning }}</p>
        <footer hlmDialogFooter>
          <button
            hlmBtn
            [variant]="'outline'"
            type="button"
            data-rotation-cancel
            (click)="opened.set(false)"
          >
            {{ cancel }}
          </button>
          <button
            hlmBtn
            [variant]="'destructive'"
            type="button"
            data-rotation-confirm-submit
            (click)="confirm()"
          >
            {{ copy.rotate }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class RotateConfirmDialogComponent {
  readonly reference = input.required<IngestTokenView>();
  readonly projectName = input.required<string>();
  readonly confirmed = output<void>();
  readonly dismissed = output<void>();
  protected readonly opened = signal(true);
  protected readonly copy = SECRETS_COPY;
  protected readonly title = $localize`:@@settings.secrets.confirm-title:Rotate this credential?`;
  protected readonly referenceLabel = $localize`:@@settings.secrets.confirm-reference:Secret reference`;
  protected readonly warning = $localize`:@@settings.secrets.confirm-warning:Rotation creates a replacement credential. The previous credential stops working according to the server's rotation policy; an overlap window is not guaranteed. Update downstream CI and verify the replacement there; rotation does not update CI for you.`;
  protected readonly revealWarning = $localize`:@@settings.secrets.confirm-reveal-warning:The new value is shown once after rotation. Store it securely before dismissing; it cannot be retrieved again.`;
  protected readonly cancel = $localize`:@@settings.secrets.confirm-cancel:Cancel`;
  private accepted = false;
  private finished = false;
  protected confirm(): void {
    if (!this.opened()) return;
    this.accepted = true;
    this.opened.set(false);
  }
  protected stateChanged(state: 'open' | 'closed'): void {
    if (state === 'closed') this.opened.set(false);
  }
  protected finish(): void {
    if (this.finished) return;
    this.finished = true;
    if (this.accepted) this.confirmed.emit();
    else this.dismissed.emit();
  }
}
