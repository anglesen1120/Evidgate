import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  computed,
  effect,
  inject,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import type { PublishedRevision } from '../../../core/data/policy-data.service';
import { isApiRequestError, type ApiError } from '../../../core/http/api-error';
import { SessionStore } from '../../../core/session/session.store';
import { ConflictDialogComponent } from '../../../shared/ui/conflict/conflict-dialog.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow';
import {
  conflictFromError,
  type ConflictField,
} from '../../../shared/ui/conflict/conflict-details';
import { clipDiffText, diffPolicyBodies } from './policy-diff';
import { POLICY_BODY_MAX_BYTES, policyBodyBytes } from './policy-body-size';
import { PolicyStore } from './policy.store';

// Bound the field list because the shared conflict dialog renders every supplied row.
const MAX_CONFLICT_FIELDS = 100;

@Component({
  selector: 'app-policy-publish-dialog',
  imports: [
    HlmAlertImports,
    HlmButton,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    ConflictDialogComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [disableClose]="submitting() || store.publishing()"
      [closeOnOutsidePointerEvents]="false"
      [restoreFocus]="false"
      (stateChanged)="onStateChanged($event)"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        data-policy-publish-dialog
        [attr.aria-busy]="busy()"
        class="max-h-[calc(100dvh-var(--eg-space-8))] overflow-y-auto [&_p]:m-0"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle>{{ copy.title }}</h2>
          <p hlmDialogDescription>{{ copy.description }}</p>
        </header>
        <div class="grid min-w-0 gap-2">
          <label hlmLabel for="policy-publish-body">{{ copy.body }}</label>
          <textarea
            hlmInput
            class="min-h-[var(--eg-space-12)] resize-y font-mono"
            #textarea
            id="policy-publish-body"
            rows="14"
            spellcheck="false"
            [value]="text()"
            (input)="edit($event)"
            [disabled]="busy() || !session.has('policy:write')"
            [attr.aria-invalid]="localError() ? 'true' : null"
            [aria-describedby]="
              localError() ? 'policy-body-size policy-local-error' : 'policy-body-size'
            "
          ></textarea>
          <p id="policy-body-size" class="text-xs text-muted-foreground">
            {{ copy.bytes }}: {{ bytes() ?? '—' }} / {{ maxBytes }}
          </p>
          <p role="status">
            @if (loading()) {
              {{ copy.loading }}
            } @else if (store.publishing()) {
              {{ copy.publishing }}
            }
          </p>
          @if (localError(); as error) {
            <p
              hlmAlert
              [variant]="'destructive'"
              id="policy-local-error"
              data-policy-publish-local-error
              role="alert"
            >
              {{ error }}
            </p>
          }
          @if (seedFailed()) {
            <p hlmAlert [variant]="'destructive'" role="alert">{{ copy.seedError }}</p>
          }
          @if (serverError(); as error) {
            <div
              hlmAlert
              [variant]="'destructive'"
              class="wrap-anywhere"
              data-policy-error
              [attr.data-error-code]="error.code"
              role="alert"
            >
              <code>{{ error.code }}</code>
              <p>{{ error.message }}</p>
              <ul>
                @for (detail of error.details; track $index) {
                  <li>
                    <code>{{ detail.path }}</code>
                    <p>{{ detail.reason }}</p>
                  </li>
                }
              </ul>
            </div>
          }
        </div>
        <footer hlmDialogFooter>
          <button
            hlmBtn
            [variant]="'outline'"
            class="min-h-[var(--eg-control-touch)] whitespace-normal"
            type="button"
            [disabled]="submitting() || store.publishing()"
            (click)="requestClose()"
          >
            {{ copy.cancel }}
          </button>
          <button
            type="button"
            hlmBtn
            class="min-h-[var(--eg-control-touch)] whitespace-normal"
            data-policy-publish-submit
            [disabled]="busy() || seedFailed() || !session.has('policy:write')"
            (click)="submit()"
          >
            {{ copy.publish }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
    <app-conflict-dialog
      [open]="conflictOpen()"
      [fields]="conflictFields()"
      [currentRevision]="currentRevision()"
      [reloadState]="reloadState()"
      [reloadError]="reloadError()"
      (reload)="reload()"
      (keepEditing)="keepEditing()"
    />
  `,
})
export class PublishDialogComponent {
  protected readonly store = inject(PolicyStore);

  protected readonly session = inject(SessionStore);

  protected readonly mutationPolicy = inject(NarrowMutationPolicy);

  private readonly injector = inject(Injector);

  readonly completed = output<PublishedRevision>();
  readonly reloaded = output<void>();
  protected readonly opened = signal(false);
  protected readonly text = signal('');
  protected readonly loading = signal(false);
  protected readonly submitting = signal(false);
  protected readonly busy = computed(
    () => this.loading() || this.submitting() || this.store.publishing(),
  );
  protected readonly maxBytes = POLICY_BODY_MAX_BYTES;
  // Count the compact wire body, not the editor's pretty-printed whitespace.
  protected readonly bytes = computed(() => policyBodyBytes(this.text()));
  protected readonly localError = signal<string | null>(null);
  protected readonly serverError = signal<ApiError | null>(null);
  protected readonly seedFailed = signal(false);
  protected readonly conflictOpen = signal(false);
  protected readonly conflictFields = signal<readonly ConflictField[]>([]);
  protected readonly currentRevision = signal<string | null>(null);
  protected readonly reloadState = signal<'idle' | 'loading' | 'error'>('idle');
  protected readonly reloadError = signal<ApiError | null>(null);
  private readonly textarea = viewChild<ElementRef<HTMLTextAreaElement>>('textarea');
  private opener: HTMLElement | null = null;
  private generation = 0;
  protected readonly copy = {
    title: $localize`:@@settings.policy.publish-title:Publish revision`,
    description: $localize`:@@settings.policy.publish-description:Review the JSON policy body before publishing. Publishing does not change the activation mode.`,
    body: $localize`:@@settings.policy.body:Policy body (JSON object)`,
    bytes: $localize`:@@settings.policy.bytes:UTF-8 bytes`,
    loading: $localize`:@@settings.policy.seed-loading:Loading policy body…`,
    publishing: $localize`:@@settings.policy.publishing:Publishing revision…`,
    seedError: $localize`:@@settings.policy.seed-error:The policy body could not be loaded. Close and reopen to try again.`,
    invalid: $localize`:@@settings.policy.invalid-json:Enter a valid JSON object.`,
    tooLarge: $localize`:@@settings.policy.too-large:The policy body exceeds the 65536-byte limit.`,
    cancel: $localize`:@@settings.policy.cancel:Cancel`,
    publish: $localize`:@@settings.policy.publish-title:Publish revision`,
    notSet: $localize`:@@settings.policy.conflict-not-set:(not set)`,
  };
  constructor() {
    effect(() => {
      if (!this.session.has('policy:write')) this.close();
    });
    inject(DestroyRef).onDestroy(() => this.close());
  }
  async open(revisionId: string, opener: HTMLElement): Promise<void> {
    if (
      !this.mutationPolicy.allowsMutation() ||
      !this.session.has('policy:write') ||
      this.busy() ||
      this.opened() ||
      this.conflictOpen()
    )
      return;
    const generation = ++this.generation;
    this.opener = opener;
    this.text.set('');
    this.localError.set(null);
    this.serverError.set(null);
    this.seedFailed.set(false);
    this.loading.set(true);
    this.opened.set(true);
    try {
      const revision = await this.store.loadBody(revisionId);
      if (generation !== this.generation) return;
      this.text.set(JSON.stringify(revision.body, null, 2) ?? '');
    } catch (error: unknown) {
      if (generation !== this.generation) return;
      this.seedFailed.set(true);
      if (isApiRequestError(error)) this.serverError.set(error.error);
    } finally {
      if (generation === this.generation) {
        this.loading.set(false);
        this.focusTextarea();
      }
    }
  }
  close(): void {
    this.generation++;
    this.opened.set(false);
    this.conflictOpen.set(false);
    this.loading.set(false);
    this.submitting.set(false);
    this.text.set('');
    this.serverError.set(null);
    this.localError.set(null);
    this.conflictFields.set([]);
    if (this.opener?.isConnected) this.opener.focus();
    this.opener = null;
  }
  protected edit(event: Event): void {
    if (!(event.target instanceof HTMLTextAreaElement)) return;
    this.text.set(event.target.value);
    this.localError.set(null);
    // The 422 describes the body that was sent; the first keystroke makes it a stale claim.
    this.serverError.set(null);
  }
  protected requestClose(): void {
    if (!this.submitting() && !this.store.publishing()) this.close();
  }
  protected onStateChanged(state: 'open' | 'closed'): void {
    if (state === 'closed' && this.opened()) this.requestClose();
  }
  protected async submit(): Promise<void> {
    if (!this.canSubmit()) return;
    this.localError.set(null);
    this.serverError.set(null);
    // Pretty-printing must not reject a body that fits the compact wire limit.
    const bytes = policyBodyBytes(this.text());
    if (bytes !== null && bytes > POLICY_BODY_MAX_BYTES) {
      this.localError.set(this.copy.tooLarge);
      this.focusTextarea();
      return;
    }
    let parsed: unknown;
    try {
      parsed = JSON.parse(this.text());
      if (parsed === null || typeof parsed !== 'object' || Array.isArray(parsed))
        throw new Error('invalid_json');
    } catch {
      this.localError.set(this.copy.invalid);
      this.focusTextarea();
      return;
    }
    const generation = this.generation;
    this.submitting.set(true);
    try {
      const result = await this.store.publish(this.text());
      if (generation !== this.generation) return;
      switch (result.kind) {
        case 'created':
        case 'unchanged':
          this.close();
          this.completed.emit({ revision: result.revision, created: result.kind === 'created' });
          break;
        case 'invalid_json':
          this.localError.set(this.copy.invalid);
          break;
        case 'too_large':
          this.localError.set(this.copy.tooLarge);
          break;
        case 'refused':
          this.serverError.set(result.error);
          break;
        case 'conflict':
          await this.presentConflict(result.error, result.currentRevisionId, parsed);
          break;
        case 'busy':
          break;
      }
    } finally {
      if (generation === this.generation) {
        this.submitting.set(false);
        if (this.opened()) this.focusTextarea();
      }
    }
  }
  private canSubmit(): boolean {
    return (
      this.mutationPolicy.allowsMutation() &&
      this.opened() &&
      !this.busy() &&
      !this.seedFailed() &&
      this.session.has('policy:write')
    );
  }

  // The conflict envelope carries metadata, not the current body. Read that revision once
  // and compare it with the retained draft; a failed read keeps the envelope's fallback fields.
  private async presentConflict(
    error: ApiError,
    currentRevisionId: string | null,
    attempted: unknown,
  ): Promise<void> {
    const generation = this.generation;
    let fields: readonly ConflictField[] | null = null;
    if (currentRevisionId !== null) {
      try {
        const head = await this.store.loadBody(currentRevisionId);
        if (generation !== this.generation) return;
        const differences = diffPolicyBodies(head.body, attempted).rows;
        if (differences.length > 0) {
          // Array-set changes share a pointer; include the index to keep tracked keys unique.
          fields = differences.slice(0, MAX_CONFLICT_FIELDS).map((row, index) => ({
            id: `${index}:${row.path}`,
            label: clipDiffText(row.path),
            local: row.after ?? this.copy.notSet,
            server: row.before ?? this.copy.notSet,
          }));
        }
      } catch {
        // A refused read is not a failed publish: the shared dialog still opens, with the
        // envelope's own (uncompared) fields and a working Reload.
        if (generation !== this.generation) return;
        fields = null;
      }
    }
    this.conflictFields.set(
      fields ?? conflictFromError(error, { body: this.text() }, { body: this.copy.body }).fields,
    );
    this.currentRevision.set(currentRevisionId);
    this.reloadState.set('idle');
    this.reloadError.set(null);
    this.opened.set(false);
    this.conflictOpen.set(true);
  }
  protected keepEditing(): void {
    // A dismissed reload must not later close an editor the user has returned to.
    this.generation++;
    this.conflictOpen.set(false);
    if (!this.session.has('policy:write')) {
      this.close();
      return;
    }
    this.opened.set(true);
    this.focusTextarea();
  }
  protected async reload(): Promise<void> {
    if (this.reloadState() === 'loading') return;
    const generation = this.generation;
    this.reloadState.set('loading');
    this.reloadError.set(null);
    await this.store.reload();
    if (generation !== this.generation) return;
    if (this.store.phase() === 'error' || this.store.phase() === 'not_found') {
      this.reloadError.set(this.store.loadError());
      this.reloadState.set('error');
      return;
    }
    this.reloadState.set('idle');
    this.close();
    this.reloaded.emit();
  }
  private focusTextarea(): void {
    afterNextRender(
      () => {
        if (this.opened()) this.textarea()?.nativeElement.focus();
      },
      { injector: this.injector },
    );
  }
}
