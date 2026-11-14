import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import type { ApiTokenView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { createIntent, type Intent } from '../../../core/http/intent';
import { ApiTokensStore } from './api-tokens.store';
import { API_TOKENS_WRITE_COPY } from './api-tokens-write.copy';
import { ApiTokenWriteErrorComponent } from './api-token-write-error.component';
import { retryReadyFor } from './api-token-retry';
import { ApiTokenFactsComponent } from './api-token-facts.component';
import { API_TOKENS_COPY } from './api-tokens.copy';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';

@Component({
  selector: 'app-api-token-revoke-dialog',
  imports: [HlmButton, HlmDialogImports, ApiTokenWriteErrorComponent, ApiTokenFactsComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [closeOnOutsidePointerEvents]="false"
      [disableClose]="true"
      [restoreFocus]="false"
      [autoFocus]="'[data-api-revoke-cancel]'"
      [aria-label]="copy.revokeTitle"
      (closed)="finishClose()"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        data-api-revoke-dialog
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere"
        (keydown.escape)="escape($event)"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ copy.revokeTitle }}</h2>
        </header>
        <p hlmDialogDescription>{{ copy.revokeWarning }}</p>
        <p class="wrap-anywhere">{{ projectLabel }}: {{ projectName() }}</p>
        <app-api-token-facts [row]="row()" />
        <app-api-token-write-error [error]="store.revokeError()" />
        <footer hlmDialogFooter class="mt-3">
          <button
            hlmBtn
            [variant]="'outline'"
            type="button"
            data-api-revoke-cancel
            [disabled]="store.revoking()"
            (click)="close()"
          >
            {{ copy.cancel }}
          </button>
          <button
            hlmBtn
            [variant]="'destructive'"
            type="button"
            data-api-revoke-submit
            [disabled]="
              store.revoking() ||
              !viewport.canMutate() ||
              !store.canWrite() ||
              !retryReady() ||
              (store.revokeError() !== null && !store.revokeError()?.retryable)
            "
            (click)="submit()"
          >
            {{ store.revoking() ? copy.submitting : copy.revoke }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class ApiTokenRevokeDialogComponent {
  readonly row = input.required<ApiTokenView>();
  readonly dismissed = output<void>();
  readonly completed = output<void>();
  protected readonly store = inject(ApiTokensStore);
  protected readonly viewport = inject(NarrowMutationPolicy);
  protected readonly projectLabel = API_TOKENS_COPY.project;
  protected readonly projectName = (): string =>
    this.store.session.projects().find((project) => project.id === this.store.selectedProject())
      ?.name ?? '';
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  protected readonly copy = API_TOKENS_WRITE_COPY;
  protected readonly opened = signal(true);
  protected readonly retryReady = retryReadyFor(this.store.revokeError);
  private readonly opener =
    this.document.activeElement instanceof HTMLElement ? this.document.activeElement : null;
  private intent: Intent | null = null;

  constructor() {
    this.store.resetRevokeError();
  }
  protected async submit(): Promise<void> {
    if (
      !this.opened() ||
      !this.viewport.allowsMutation() ||
      !this.store.canWrite() ||
      this.store.revoking() ||
      !this.retryReady()
    )
      return;
    if (this.store.revokeError() && !this.store.revokeError()?.retryable) return;
    this.intent ??= createIntent();
    if (await this.store.revoke(this.row().id, this.intent)) {
      if (this.destroyRef.destroyed) return;
      this.completed.emit();
      this.close();
    }
  }
  close(): void {
    this.opened.set(false);
  }
  protected escape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    if (!this.store.revoking()) this.close();
  }
  protected finishClose(): void {
    this.dismissed.emit();
    if (this.opener?.isConnected && !this.opener.matches(':disabled')) this.opener.focus();
    else
      this.document.querySelector<HTMLElement>('app-api-tokens-page app-section-header')?.focus();
  }
}
