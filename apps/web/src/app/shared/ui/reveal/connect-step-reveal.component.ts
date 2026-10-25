// 09.2.1-T2.2; design: docs/design/web/state-sheet.md (recipes A/W), tokens.md and onboarding.md.
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
  type ElementRef,
} from '@angular/core';

let nextRevealId = 0;

@Component({
  selector: 'app-connect-step-reveal',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrls: ['../states/state-panel.css', './connect-step-reveal.component.css'],
  template: `
    <section class="reveal" [attr.aria-labelledby]="headingId">
      <h3 [id]="headingId">{{ heading() }}</h3>
      <p class="reveal__warning" [id]="warningId">{{ warning() }}</p>
      <input
        #control
        data-testid="reveal-value"
        type="text"
        readonly
        autocomplete="off"
        spellcheck="false"
        [attr.aria-labelledby]="headingId"
        [attr.aria-describedby]="warningId"
        [value]="value()"
      />
      <div class="reveal__actions">
        <button
          class="state-panel__action"
          data-testid="reveal-copy"
          type="button"
          [disabled]="copying()"
          [attr.aria-busy]="copying()"
          (click)="copy()"
        >
          {{ copyLabel() }}
        </button>
        <button
          class="state-panel__action"
          data-testid="reveal-dismiss"
          type="button"
          (click)="dismissed.emit()"
        >
          {{ dismissLabel() }}
        </button>
      </div>
      <p
        data-testid="reveal-copy-status"
        role="status"
        [attr.aria-live]="'polite'"
        aria-atomic="true"
      >
        {{ copyStatus() }}
      </p>
    </section>
  `,
})
export class ConnectStepRevealComponent {
  readonly heading = input.required<string>();
  readonly value = input.required<string>();
  // Also the clipboard-failure announcement: the caller supplies manual-copy guidance here.
  readonly warning = input.required<string>();
  readonly copyLabel = input.required<string>();
  readonly copiedLabel = input.required<string>();
  readonly dismissLabel = input.required<string>();
  readonly dismissed = output<void>();

  protected readonly headingId = `connect-reveal-heading-${++nextRevealId}`;
  protected readonly warningId = `${this.headingId}-warning`;
  protected readonly copying = signal(false);
  protected readonly copyStatus = signal('');
  private readonly control = viewChild<ElementRef<HTMLInputElement>>('control');
  private readonly destroyRef = inject(DestroyRef);
  private attempt = 0;

  constructor() {
    effect(() => {
      this.value();
      this.attempt++;
      this.copying.set(false);
      this.copyStatus.set('');
    });
    this.destroyRef.onDestroy(() => {
      this.attempt++;
      // Remove the rendered value as well as ignoring late clipboard completions.
      const control = this.control()?.nativeElement;
      if (control) control.value = '';
    });
  }

  protected async copy(): Promise<void> {
    if (this.copying()) return;
    const attempt = ++this.attempt;
    this.copying.set(true);
    this.copyStatus.set('');
    try {
      await navigator.clipboard.writeText(this.value());
      if (this.destroyRef.destroyed || attempt !== this.attempt) return;
      this.copyStatus.set(this.copiedLabel());
    } catch {
      if (this.destroyRef.destroyed || attempt !== this.attempt) return;
      const control = this.control()?.nativeElement;
      if (control) {
        control.focus();
        control.select();
      }
      this.copyStatus.set(this.warning());
    } finally {
      if (!this.destroyRef.destroyed && attempt === this.attempt) this.copying.set(false);
    }
    // Status remains until the next attempt/value; no timer retains the component or secret.
  }
}
