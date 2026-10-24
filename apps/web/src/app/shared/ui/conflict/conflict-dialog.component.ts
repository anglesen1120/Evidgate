import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  afterRenderEffect,
  computed,
  inject,
  input,
  output,
  viewChild,
} from '@angular/core';

import type { ApiError } from '../../../core/http/api-error';
import type { ConflictField } from './conflict-details';

let nextDialogId = 0;

@Component({
  selector: 'app-conflict-dialog',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './conflict-dialog.component.css',
  template: `
    <!-- Native dialog backdrop has no focusable wrapper; Escape/cancel is the keyboard path. -->
    <!-- eslint-disable-next-line @angular-eslint/template/interactive-supports-focus -->
    <dialog
      #dialog
      [attr.aria-labelledby]="titleId"
      (cancel)="cancel($event)"
      (keydown.escape)="cancel($event)"
      (click)="backdropClick($event)"
    >
      <header>
        <div>
          <h2 [id]="titleId" i18n="@@shared.conflict.title">Review conflict</h2>
          <p i18n="@@shared.conflict.description">
            Reload is explicit. Your draft is not merged or resubmitted automatically.
          </p>
        </div>
        <button class="close" type="button" (click)="dismiss()" i18n="@@shared.conflict.close">
          Close
        </button>
      </header>
      <div class="body" [attr.aria-busy]="reloadState() === 'loading'">
        <dl class="revision">
          <dt i18n="@@shared.conflict.current-revision">Current revision</dt>
          <dd>
            @if (currentRevision(); as revision) {
              <code>{{ revision }}</code>
            } @else {
              <span i18n="@@shared.conflict.revision-unavailable">Revision unavailable</span>
            }
          </dd>
        </dl>
        @if (comparisonUnavailable()) {
          <p i18n="@@shared.conflict.comparison-unavailable">
            The current version could not be compared
          </p>
        }
        <div class="fields">
          @for (field of fields(); track field.id) {
            <section class="field">
              <h3>
                {{ field.label }}
                @if (field.local !== null && field.server !== null) {
                  @if (field.local === field.server) {
                    <span class="marker" i18n="@@shared.conflict.same">Same</span>
                  } @else {
                    <span class="marker" i18n="@@shared.conflict.changed">Changed</span>
                  }
                } @else {
                  <span class="marker" i18n="@@shared.conflict.not-compared">Not compared</span>
                }
              </h3>
              <dl class="comparison">
                <div>
                  <dt i18n="@@shared.conflict.local">Your draft</dt>
                  <dd class="local-value">
                    @if (field.local !== null) {
                      {{ field.local }}
                    } @else {
                      <span i18n="@@shared.conflict.local-unavailable"
                        >Draft value unavailable</span
                      >
                    }
                  </dd>
                </div>
                <div>
                  <dt i18n="@@shared.conflict.server">Current server value</dt>
                  <dd class="server-value">
                    @if (field.server !== null) {
                      {{ field.server }}
                    } @else {
                      <span i18n="@@shared.conflict.server-unavailable"
                        >Server value unavailable</span
                      >
                    }
                  </dd>
                </div>
              </dl>
            </section>
          }
        </div>
        @if (reloadError(); as error) {
          <section class="error" [attr.aria-live]="'polite'">
            <h3 i18n="@@shared.conflict.reload-error">The current version could not be reloaded</h3>
            <p>{{ error.message }}</p>
            <dl>
              <div>
                <dt i18n="@@shared.conflict.error-code">Error code</dt>
                <dd>
                  <code>{{ error.code }}</code>
                </dd>
              </div>
              @if (error.requestId; as requestId) {
                <div>
                  <dt i18n="@@shared.conflict.request-id">Request id</dt>
                  <dd>
                    <code>{{ requestId }}</code>
                  </dd>
                </div>
              }
            </dl>
          </section>
        }
      </div>
      <p class="progress" role="status" [attr.aria-live]="'polite'">
        @if (reloadState() === 'loading') {
          <span i18n="@@shared.conflict.reloading">Reloading…</span>
        }
      </p>
      <footer>
        <button type="button" (click)="dismiss()" i18n="@@shared.conflict.keep-editing">
          Keep editing
        </button>
        <button
          class="reload"
          type="button"
          [disabled]="reloadState() === 'loading'"
          (click)="requestReload()"
          i18n="@@shared.conflict.reload"
        >
          Reload current
        </button>
      </footer>
    </dialog>
  `,
})
export class ConflictDialogComponent {
  readonly open = input(false);
  readonly fields = input<readonly ConflictField[]>([]);
  readonly currentRevision = input<string | null>(null);
  readonly reloadState = input<'idle' | 'loading' | 'error'>('idle');
  readonly reloadError = input<ApiError | null>(null);
  readonly reload = output<void>();
  readonly keepEditing = output<void>();

  protected readonly titleId = `conflict-title-${nextDialogId++}`;
  protected readonly comparisonUnavailable = computed(
    () => this.fields().length === 0 || this.fields().every((field) => field.server === null),
  );
  private readonly dialog = viewChild<ElementRef<HTMLDialogElement>>('dialog');
  private opener: HTMLElement | null = null;

  constructor() {
    // Native showModal supplies top-layer semantics and Tab/Shift+Tab containment.
    afterRenderEffect(() => {
      const dialog = this.dialog()?.nativeElement;
      if (!dialog) return;
      if (this.open() && !dialog.open) {
        const active = dialog.ownerDocument.activeElement;
        this.opener = active instanceof HTMLElement ? active : null;
        dialog.showModal();
      } else if (!this.open() && dialog.open) {
        this.closeDialog();
      }
    });
    inject(DestroyRef).onDestroy(() => this.closeDialog());
  }

  protected requestReload(): void {
    if (this.reloadState() !== 'loading' && this.dialog()?.nativeElement.open) this.reload.emit();
  }

  protected cancel(event: Event): void {
    event.preventDefault();
    this.dismiss();
  }

  protected backdropClick(event: MouseEvent): void {
    const dialog = this.dialog()?.nativeElement;
    if (!dialog || event.target !== dialog) return;
    const bounds = dialog.getBoundingClientRect();
    if (
      event.clientX < bounds.left ||
      event.clientX > bounds.right ||
      event.clientY < bounds.top ||
      event.clientY > bounds.bottom
    ) {
      this.dismiss();
    }
  }

  protected dismiss(): void {
    if (!this.dialog()?.nativeElement.open) return;
    this.closeDialog();
    this.keepEditing.emit();
  }

  private closeDialog(): void {
    const dialog = this.dialog()?.nativeElement;
    if (dialog?.open) dialog.close();
    const opener = this.opener;
    this.opener = null;
    if (opener?.isConnected) opener.focus();
    // After a successful reload the owning view closes this input, then focuses
    // its heading. This component restores the opener only; it never navigates.
  }
}
