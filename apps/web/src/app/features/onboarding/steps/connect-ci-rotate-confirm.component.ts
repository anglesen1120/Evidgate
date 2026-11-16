import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  untracked,
  viewChild,
} from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';
import { DesktopMutationDirective, NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { sanitizeLabel } from './default-policy';

export interface RotateScope {
  readonly orgId: string;
  readonly projectId: string;
}

@Component({
  selector: 'app-connect-ci-rotate-confirm',
  imports: [HlmButton, HlmDialogImports, DesktopMutationDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <hlm-dialog
      #rotation
      class="block min-w-0"
      [state]="active() && !disabled() && policy.canMutate() ? null : 'closed'"
      [role]="'alertdialog'"
      [autoFocus]="'[data-ci-rotation-cancel]'"
      [restoreFocus]="true"
      [closeOnOutsidePointerEvents]="false"
      (stateChanged)="rememberScope($event)"
      (closed)="finish($event)"
    >
      <button
        *appDesktopMutation
        hlmDialogTrigger
        hlmBtn
        [variant]="'outline'"
        class="h-auto min-h-(--eg-control-touch) max-w-full justify-self-start whitespace-normal wrap-anywhere"
        type="button"
        data-testid="connect-token-rotate"
        [attr.aria-label]="triggerLabel()"
        [attr.aria-describedby]="describedBy()"
        [disabled]="disabled()"
      >
        {{ label() }}
      </button>
      <hlm-dialog-content
        *hlmDialogPortal
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle i18n="@@onboarding.connect.rotate-confirm-title">
            Rotate this ingest token?
          </h2>
        </header>
        <p hlmDialogDescription i18n="@@onboarding.connect.rotate-confirm-warning">
          Rotation creates a replacement token. The previous token stops working according to the
          server's rotation policy; an overlap window is not guaranteed. Update CI and verify the
          replacement there; rotation does not update CI for you.
        </p>
        <dl
          class="m-0 grid min-w-0 gap-(--eg-space-4) [&_dd]:m-0 [&_dd]:wrap-anywhere [&_dt]:text-muted-foreground"
        >
          <div>
            <dt i18n="@@onboarding.connect.token-title">Ingest token</dt>
            <dd>
              <bdi>{{ name() }}</bdi>
            </dd>
          </div>
          <div>
            <dt i18n="@@onboarding.connect.rotate-confirm-project">Project</dt>
            <dd>
              <bdi>{{ project() }}</bdi>
            </dd>
          </div>
          <div>
            <dt i18n="@@onboarding.connect.prefix">Prefix</dt>
            <dd>
              <code
                ><bdi>{{ prefix() }}</bdi></code
              >
            </dd>
          </div>
          <div>
            <dt i18n="@@onboarding.connect.scopes">Scopes</dt>
            <dd>
              <code
                ><bdi>{{ scopes() }}</bdi></code
              >
            </dd>
          </div>
        </dl>
        <p class="m-0">{{ revealWarning() }}</p>
        <footer hlmDialogFooter>
          <button
            hlmBtn
            [variant]="'outline'"
            class="h-auto min-h-(--eg-control-touch) whitespace-normal wrap-anywhere"
            type="button"
            data-ci-rotation-cancel
            data-testid="connect-rotation-cancel"
            (click)="rotation.close()"
            i18n="@@onboarding.connect.rotate-confirm-cancel"
          >
            Cancel
          </button>
          <button
            *appDesktopMutation
            hlmBtn
            [variant]="'destructive'"
            class="h-auto min-h-(--eg-control-touch) whitespace-normal wrap-anywhere"
            type="button"
            data-testid="connect-rotation-confirm"
            [disabled]="disabled()"
            (click)="rotation.close(consentScope)"
          >
            {{ label() }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class CiRotateConfirmComponent {
  readonly token = input.required<IngestTokenView>();
  readonly projectName = input.required<string>();
  readonly scope = input.required<RotateScope>();
  readonly label = input.required<string>();
  readonly triggerLabel = input.required<string>();
  readonly describedBy = input.required<string>();
  readonly revealWarning = input.required<string>();
  readonly active = input(true);
  readonly disabled = input(false);
  readonly confirmed = output<RotateScope>();
  protected readonly policy = inject(NarrowMutationPolicy);
  protected readonly name = computed(() => sanitizeLabel(this.token().name));
  protected readonly project = computed(() => sanitizeLabel(this.projectName()));
  protected readonly prefix = computed(() => sanitizeLabel(this.token().prefix));
  protected readonly scopes = computed(() => sanitizeLabel(this.token().scopes.join(' ')));
  protected consentScope: RotateScope | null = null;
  private readonly dialog = viewChild(HlmDialog);

  constructor() {
    effect(() => {
      this.scope();
      const dialog = this.dialog();
      // The dialog must not carry consent across an organisation or project change.
      if (dialog) untracked(() => dialog.close());
    });
  }

  protected rememberScope(state: 'open' | 'closed'): void {
    if (state === 'open') this.consentScope = this.scope();
  }

  protected finish(result: unknown): void {
    const scope = this.consentScope;
    this.consentScope = null;
    if (scope && result === scope && scope === this.scope()) this.confirmed.emit(scope);
  }
}
