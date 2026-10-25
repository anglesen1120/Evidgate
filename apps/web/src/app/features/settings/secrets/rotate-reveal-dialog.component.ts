import { CdkTrapFocus } from '@angular/cdk/a11y';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { ConnectStepRevealComponent } from '../../../shared/ui/reveal/connect-step-reveal.component';
import { RevealSlot } from '../../../shared/ui/reveal/one-time-reveal.slot';
import { wrapDialogFocus } from '../members/dialog-focus-wrap';

@Component({
  selector: 'app-rotate-reveal-dialog',
  imports: [CdkTrapFocus, ConnectStepRevealComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './secrets.page.css',
  template: `
    <dialog
      #dialog
      tabindex="-1"
      aria-labelledby="rotation-reveal-title"
      (cancel)="cancel($event)"
      (close)="closed()"
      (keydown.tab)="wrapFocus($event)"
      (keydown.shift.tab)="wrapFocus($event)"
      [cdkTrapFocus]="opened()"
    >
      <header>
        <h2 id="rotation-reveal-title">{{ title }}</h2>
      </header>
      <div class="dialog-body">
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
            <p>
              {{ overlapLabel }} <time [attr.datetime]="date">{{ date }}</time>
            </p>
          }
        }
      </div>
    </dialog>
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
  private readonly dialog = viewChild.required<ElementRef<HTMLDialogElement>>('dialog');
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
    this.dialog().nativeElement.showModal();
  }
  close(restoreFocus = true): void {
    // Clear the live DOM synchronously, before Angular removes the shared reveal view.
    const element = this.dialog()?.nativeElement;
    element?.querySelectorAll('input').forEach((control) => {
      control.value = '';
      control.removeAttribute('value');
    });
    this.slot.clear();
    this.overlap.set(null);
    this.opened.set(false);
    if (element?.open) element.close();
    if (restoreFocus) {
      if (this.opener?.isConnected && !this.opener.matches(':disabled')) this.opener.focus();
      else this.fallback?.();
    }
    this.opener = null;
    this.fallback = null;
  }
  protected cancel(event: Event): void {
    event.preventDefault();
    this.close();
  }
  protected closed(): void {
    // Native close events are queued: an older close must not clear a newly opened reveal.
    if (!this.dialog().nativeElement.open) this.close();
  }
  protected wrapFocus(event: Event): void {
    wrapDialogFocus(event, this.dialog().nativeElement);
  }
}
