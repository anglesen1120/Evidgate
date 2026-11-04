import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { ConnectStepRevealComponent } from '../../../shared/ui/reveal/connect-step-reveal.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';

@Component({
  selector: 'app-rotate-reveal-dialog',
  imports: [HlmDialogImports, ConnectStepRevealComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [restoreFocus]="false"
      (stateChanged)="stateChanged($event)"
      (closed)="restoreFocus()"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ title }}</h2>
        </header>
        <div #revealBody class="grid min-w-0 gap-2 wrap-anywhere">
          @if (slot.current(); as entry) {
            <app-connect-step-reveal
              [heading]="heading"
              [value]="entry.value"
              [warning]="warning"
              [copyLabel]="copyLabel"
              [copiedLabel]="copied"
              [dismissLabel]="dismiss"
              (dismissed)="close()"
            />
            @if (overlap(); as date) {
              <p class="m-0 text-sm text-muted-foreground">
                {{ overlapLabel }} <time [attr.datetime]="date">{{ date }}</time>
              </p>
            }
          }
        </div>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class RotateRevealDialogComponent {
  protected readonly slot = inject(RevealSlot);
  protected readonly opened = signal(false);
  protected readonly overlap = signal<string | null>(null);
  protected readonly title = $localize`:@@settings.secrets.reveal-title:Credential rotated`;
  protected readonly heading = $localize`:@@settings.secrets.reveal-heading:Copy the new credential once`;
  protected readonly warning = $localize`:@@settings.secrets.reveal-warning:This value is shown only once. Store it securely and update your CI. If copying fails, select and copy the value manually before dismissing.`;
  protected readonly copyLabel = $localize`:@@settings.secrets.reveal-copy:Copy credential`;
  protected readonly copied = $localize`:@@settings.secrets.reveal-copied:Credential copied.`;
  protected readonly dismiss = $localize`:@@settings.secrets.reveal-dismiss:Dismiss and clear credential`;
  protected readonly overlapLabel = $localize`:@@settings.secrets.reveal-overlap:The previous token keeps working until`;
  private readonly revealBody = viewChild<ElementRef<HTMLElement>>('revealBody');
  private opener: HTMLElement | null = null;
  private fallback: (() => void) | null = null;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.close(false));
  }
  open(opener: HTMLElement, overlapEndsAt: string | null, fallback: () => void): void {
    this.opener = opener;
    this.fallback = fallback;
    this.overlap.set(overlapEndsAt);
    this.opened.set(true);
  }
  close(restoreFocus = true): void {
    // Wipe the portalled input synchronously; the closing animation may keep its DOM alive.
    this.revealBody()
      ?.nativeElement.querySelectorAll('input')
      .forEach((control) => {
        control.value = '';
        control.removeAttribute('value');
      });
    this.slot.clear();
    this.overlap.set(null);
    this.opened.set(false);
    if (!restoreFocus) {
      this.opener = null;
      this.fallback = null;
    }
  }
  protected stateChanged(state: 'open' | 'closed'): void {
    if (state === 'closed' && this.opened()) this.close();
  }
  protected restoreFocus(): void {
    if (this.opened()) return;
    // Restore only after CDK removes the trap; backdrop dismissal can otherwise reclaim focus.
    if (this.opener?.isConnected && !this.opener.matches(':disabled')) this.opener.focus();
    else this.fallback?.();
    this.opener = null;
    this.fallback = null;
  }
}
