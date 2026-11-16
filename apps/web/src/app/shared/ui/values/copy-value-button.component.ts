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
import { HlmIcon } from '@evidgate/ui/icon';

import { hasFormatChars } from './ref-name-display';

export type CopyValueButtonSize = 'sm' | 'lg';

let nextCopyId = 0;

@Component({
  selector: 'app-copy-value-button',
  imports: [HlmButton, HlmIcon],
  host: { '[class.icon-only]': 'iconOnly()' },
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <button
      hlmBtn
      type="button"
      [variant]="'ghost'"
      [size]="size()"
      [attr.data-copy-size]="size()"
      [class.icon-only]="iconOnly()"
      [attr.title]="iconOnly() ? actionLabel() : null"
      [attr.aria-label]="actionLabel()"
      [attr.aria-describedby]="statusId"
      [attr.aria-disabled]="copying() || !value()"
      [attr.aria-busy]="copying()"
      (click)="copy()"
    >
      @if (iconOnly()) {
        <hlm-icon class="copy-icon" [name]="iconName()" />
        <span class="sr-only">{{ buttonText() }}</span>
      } @else {
        {{ buttonText() }}
      }
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
    :host(.icon-only) {
      display: inline-flex;
      flex: none;
    }
    button.icon-only {
      inline-size: var(--eg-control-sm);
      padding: var(--eg-space-2);
      white-space: nowrap;
    }
    .copy-icon {
      inline-size: var(--eg-space-5);
      block-size: var(--eg-space-5);
    }
    button {
      max-inline-size: 100%;
      block-size: auto;
      min-block-size: var(--eg-control-sm);
      white-space: normal;
      overflow-wrap: anywhere;
    }
    button[data-copy-size='lg'] {
      min-block-size: var(--eg-control-touch);
      min-inline-size: var(--eg-control-touch);
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
  /** `lg` reserves a 44px target on every pointer; `sm` keeps the compact default. */
  readonly size = input<CopyValueButtonSize>('sm');
  readonly iconOnly = input(false);
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
  protected readonly iconName = computed(() => {
    if (this.copying()) return 'lucideLoader2';
    if (this.result() === 'copied') return 'lucideCheck';
    if (this.result() === 'failed') return 'lucideOctagonX';
    return 'lucideCopy';
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
      // Copying the original must never hide a character made visible by the display.
      if (hasFormatChars(value)) {
        this.copyFailed();
        return;
      }
      await navigator.clipboard.writeText(value);
      if (this.destroyRef.destroyed || attempt !== this.attempt || value !== this.value()) return;
      this.result.set('copied');
      this.status.set($localize`:@@shared.values.copy-success:${this.label()}:LABEL: copied.`);
    } catch {
      if (this.destroyRef.destroyed || attempt !== this.attempt || value !== this.value()) return;
      this.copyFailed();
    } finally {
      if (!this.destroyRef.destroyed && attempt === this.attempt) this.copying.set(false);
    }
  }

  private copyFailed(): void {
    this.result.set('failed');
    this.status.set(
      $localize`:@@shared.values.copy-error:Could not copy ${this.label()}:LABEL:. Show the full value and copy it manually.`,
    );
  }
}
