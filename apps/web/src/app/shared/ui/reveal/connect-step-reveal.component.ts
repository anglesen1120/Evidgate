import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  afterRenderEffect,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
  type ElementRef,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';

let nextRevealId = 0;

@Component({
  selector: 'app-connect-step-reveal',
  imports: [HlmButton, HlmCardImports, HlmInput],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="reveal min-w-0 max-w-full gap-(--eg-space-4) p-(--eg-space-5) wrap-anywhere"
      [attr.aria-labelledby]="headingId"
    >
      <h3
        hlmCardTitle
        [size]="subsectionHeadingSize"
        class="focus-visible:outline-[length:var(--eg-focus-width)] focus-visible:outline-ring focus-visible:outline-offset-[var(--eg-focus-offset)]"
        #headingControl
        [id]="headingId"
        tabindex="-1"
      >
        {{ heading() }}
      </h3>
      <p class="reveal__warning m-0 text-sm text-muted-foreground" [id]="warningId">
        {{ warning() }}
      </p>
      <input
        #control
        hlmInput
        class="h-auto min-h-(--eg-control-md) min-w-0 w-full max-w-full py-(--eg-space-3) font-mono pointer-coarse:min-h-(--eg-control-touch)"
        [attr.data-testid]="'reveal-value'"
        type="text"
        readonly
        autocomplete="off"
        spellcheck="false"
        [attr.aria-labelledby]="headingId"
        [aria-describedby]="warningId"
        [value]="value()"
      />
      <div class="reveal__actions flex min-w-0 max-w-full flex-wrap gap-(--eg-space-3)">
        <button
          hlmBtn
          [variant]="'outline'"
          class="h-auto min-h-(--eg-control-md) min-w-0 max-w-full whitespace-normal wrap-anywhere py-(--eg-space-3) pointer-coarse:min-h-(--eg-control-touch) aria-disabled:cursor-default aria-disabled:bg-muted aria-disabled:text-muted-foreground aria-disabled:shadow-none"
          [attr.data-testid]="'reveal-copy'"
          type="button"
          [attr.aria-disabled]="copying()"
          [attr.aria-busy]="copying()"
          (click)="copy()"
        >
          {{ copyLabel() }}
        </button>
        <button
          hlmBtn
          [variant]="'ghost'"
          class="h-auto min-h-(--eg-control-md) min-w-0 max-w-full whitespace-normal wrap-anywhere py-(--eg-space-3) pointer-coarse:min-h-(--eg-control-touch)"
          [attr.data-testid]="'reveal-dismiss'"
          type="button"
          (click)="dismissed.emit()"
        >
          {{ dismissLabel() }}
        </button>
      </div>
      <p
        class="m-0 min-h-[var(--eg-leading-sm)] text-sm text-muted-foreground"
        [attr.data-testid]="'reveal-copy-status'"
        role="status"
        [attr.aria-live]="'polite'"
        [attr.aria-atomic]="'true'"
      >
        {{ copyStatus() }}
      </p>
    </section>
  `,
})
export class ConnectStepRevealComponent {
  private readonly destroyRef = inject(DestroyRef);

  readonly heading = input.required<string>();
  readonly value = input.required<string>();
  // Also the clipboard-failure announcement: the caller supplies manual-copy guidance here.
  readonly warning = input.required<string>();
  readonly copyLabel = input.required<string>();
  readonly copiedLabel = input.required<string>();
  readonly dismissLabel = input.required<string>();
  readonly dismissed = output<void>();
  protected readonly subsectionHeadingSize = 'subsection' as const;

  protected readonly headingId = `connect-reveal-heading-${++nextRevealId}`;
  protected readonly warningId = `${this.headingId}-warning`;
  protected readonly copying = signal(false);
  protected readonly copyStatus = signal('');
  private readonly control = viewChild<ElementRef<HTMLInputElement>>('control');
  private readonly headingControl = viewChild<ElementRef<HTMLHeadingElement>>('headingControl');
  private attempt = 0;

  constructor() {
    effect(() => {
      this.value();
      this.attempt++;
      this.copying.set(false);
      this.copyStatus.set('');
    });
    afterRenderEffect(() => {
      this.value();
      this.headingControl()?.nativeElement.focus();
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
