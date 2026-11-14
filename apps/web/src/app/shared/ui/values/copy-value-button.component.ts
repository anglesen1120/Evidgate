import {
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';

let nextCopyId = 0;

@Component({
  selector: 'app-copy-value-button',
  imports: [HlmButton],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      hlmBtn
      type="button"
      [variant]="'ghost'"
      [size]="'sm'"
      [attr.aria-label]="actionLabel()"
      [attr.aria-describedby]="statusId"
      [attr.aria-disabled]="copying() || !value()"
      [attr.aria-busy]="copying()"
      (click)="copy()"
    >
      {{ buttonText() }}
    </button>
    <span
      [id]="statusId"
      class="sr-only"
      role="status"
      [attr.aria-live]="'polite'"
      aria-atomic="true"
      >{{ status() }}</span
    >
  `,
  styles: `
    :host {
      display: inline-block;
      max-inline-size: 100%;
    }
    button {
      max-inline-size: 100%;
      block-size: auto;
      min-block-size: var(--eg-control-sm);
      white-space: normal;
      overflow-wrap: anywhere;
    }
    @media (pointer: coarse) {
      button {
        min-block-size: var(--eg-control-touch);
        min-inline-size: var(--eg-control-touch);
      }
    }
  `,
})
export class CopyValueButtonComponent {
  readonly value = input.required<string>();
  readonly label = input.required<string>();
  readonly copyLabel = input<string>();
  protected readonly statusId = `copy-value-status-${++nextCopyId}`;
  protected readonly status = signal('');
  protected readonly copying = signal(false);
  private readonly result = signal<'idle' | 'copied' | 'failed'>('idle');
  private readonly destroyRef = inject(DestroyRef);
  private attempt = 0;
  protected readonly actionLabel = computed(
    () => this.copyLabel() ?? $localize`:@@shared.values.copy-label:Copy ${this.label()}:LABEL:`,
  );
  protected readonly buttonText = computed(() => {
    if (this.copying()) return $localize`:@@shared.values.copying:Copying…`;
    if (this.result() === 'copied') return $localize`:@@shared.values.copied:Copied`;
    if (this.result() === 'failed') return $localize`:@@shared.values.copy-failed:Copy failed`;
    return $localize`:@@shared.values.copy:Copy`;
  });

  constructor() {
    effect(() => {
      this.value();
      this.label();
      this.attempt++;
      this.copying.set(false);
      this.result.set('idle');
      this.status.set('');
    });
  }

  protected async copy(): Promise<void> {
    if (this.copying() || !this.value()) return;
    const attempt = ++this.attempt;
    const value = this.value();
    this.copying.set(true);
    this.status.set('');
    try {
      await navigator.clipboard.writeText(value);
      if (this.destroyRef.destroyed || attempt !== this.attempt || value !== this.value()) return;
      this.result.set('copied');
      this.status.set($localize`:@@shared.values.copy-success:${this.label()}:LABEL: copied.`);
    } catch {
      if (this.destroyRef.destroyed || attempt !== this.attempt || value !== this.value()) return;
      this.result.set('failed');
      this.status.set(
        $localize`:@@shared.values.copy-error:Could not copy ${this.label()}:LABEL:. Show the full value and copy it manually.`,
      );
    } finally {
      if (!this.destroyRef.destroyed && attempt === this.attempt) this.copying.set(false);
    }
  }
}
