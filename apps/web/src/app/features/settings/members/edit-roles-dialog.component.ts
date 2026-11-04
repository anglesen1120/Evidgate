import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmButton } from '@evidgate/ui/button';
import { HlmAlertImports } from '@evidgate/ui/alert';
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
import type { MembershipView } from '@evidgate/sdk';
import { createIntent, type Intent } from '../../../core/http/intent';
import { SessionStore } from '../../../core/session/session.store';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { MEMBERS_COPY } from './member-list.component';
import { MembersStore, type MutationState, type RoleChange } from './members.store';

type RoleOperation = Pick<RoleChange, 'kind' | 'roleId'>;

@Component({
  selector: 'app-edit-roles-dialog',
  imports: [
    HlmCheckbox,
    HlmLabel,
    HlmDialogImports,
    HlmButton,
    HlmAlertImports,
    RetryAfterComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <hlm-dialog
      [state]="opened() ? 'open' : 'closed'"
      [closeOnOutsidePointerEvents]="false"
      [disableClose]="true"
      [restoreFocus]="false"
      [aria-modal]="true"
      [aria-label]="copy.editTitle"
    >
      <hlm-dialog-content
        *hlmDialogPortal
        #dialog
        [attr.aria-busy]="busy()"
        [attr.data-members-dialog]="'edit-roles'"
        [showCloseButton]="false"
        class="max-h-[calc(100dvh-var(--eg-space-10))] overflow-y-auto wrap-anywhere [&_p]:m-0 [&_code]:font-mono"
        (keydown.escape)="cancel($event)"
      >
        <header hlmDialogHeader>
          <h2 hlmDialogTitle id="members-edit-title">{{ copy.editTitle }}</h2>
        </header>
        <div class="dialog-body grid min-w-0 gap-3 text-sm">
          <p hlmDialogDescription>{{ copy.editDescription }}</p>
          <p>
            <strong>{{ member()?.invited_email }}</strong>
          </p>
          <fieldset
            class="m-0 min-w-0 rounded-md border border-solid border-border p-2"
            [disabled]="busy() || !session.has('membership:write')"
          >
            <legend>{{ copy.editRolesLegend }}</legend>
            @for (role of store.roles(); track role.id) {
              <label
                hlmLabel
                class="role-option flex min-h-[var(--eg-control-touch)] items-center gap-2"
                [for]="'edit-role-' + role.id"
              >
                <hlm-checkbox
                  [inputId]="'edit-role-' + role.id"
                  data-members-role-option
                  [attr.data-role-id]="role.id"
                  [disabled]="busy() || !session.has('membership:write')"
                  [checked]="selected().has(role.id)"
                  (checkedChange)="toggle(role.id, $event)"
                />{{ role.name }}
              </label>
            }
          </fieldset>
          @if (store.roles().length === 0) {
            <p>{{ copy.rolesUnavailableEdit }}</p>
          }
          <p>{{ copy.authority }}</p>
          @if (store.editState().error; as error) {
            <div
              hlmAlert
              [variant]="'destructive'"
              class="grid gap-2 whitespace-pre-wrap"
              data-members-error
              role="alert"
            >
              <p>{{ error.message }}</p>
              <code data-members-error-code>{{ error.code }}</code>
              @if (error.code === 'last_admin') {
                <p data-members-last-admin-hint>{{ copy.lastAdminHint }}</p>
              }
              @if (store.editState().phase === 'failed' && store.editApplied().length > 0) {
                <div data-members-applied-summary>
                  <p>{{ copy.partialCurrent }}</p>
                  <div>
                    <h3>{{ copy.applied }}</h3>
                    <ul>
                      @for (change of store.editApplied(); track operationKey(change)) {
                        <li data-members-applied-item>
                          {{ change.kind === 'grant' ? copy.roleGranted : copy.roleRevoked }}:
                          {{ roleName(change.roleId) }}
                        </li>
                      }
                    </ul>
                  </div>
                  <div>
                    <h3>{{ copy.notApplied }}</h3>
                    <ul>
                      @for (change of notApplied(); track operationKey(change)) {
                        <li data-members-not-applied-item>
                          {{ change.kind === 'grant' ? copy.roleGranted : copy.roleRevoked }}:
                          {{ roleName(change.roleId) }}
                        </li>
                      }
                    </ul>
                  </div>
                </div>
              }
            </div>
            @if (error.kind === 'rate_limited' || error.kind === 'unavailable') {
              <app-retry-after
                [kind]="error.kind"
                [retryAfterSeconds]="error.retryAfterSeconds"
                [retryEligible]="retryReady() && error.retryable && canSave()"
                [busy]="busy()"
                [code]="error.code"
                [requestId]="error.requestId"
                (retry)="submit()"
              />
            }
          }
        </div>
        <footer hlmDialogFooter class="mt-4">
          <button
            hlmBtn
            [variant]="'outline'"
            #cancelButton
            type="button"
            [disabled]="busy()"
            (click)="requestClose()"
          >
            {{ copy.cancel }}
          </button>
          <button
            hlmBtn
            [variant]="'default'"
            type="button"
            data-members-submit
            [disabled]="!canSave()"
            (click)="submit()"
          >
            {{ busy() ? copy.saving : copy.save }}
          </button>
        </footer>
      </hlm-dialog-content>
    </hlm-dialog>
  `,
})
export class EditRolesDialogComponent {
  protected readonly store = inject(MembersStore);
  protected readonly session = inject(SessionStore);
  protected readonly copy = MEMBERS_COPY;
  readonly completed = output<void>();
  protected readonly opened = signal(false);
  protected readonly member = signal<MembershipView | null>(null);
  protected readonly selected = signal<ReadonlySet<string>>(new Set());
  protected readonly busy = computed(() => this.store.editState().phase === 'submitting');
  protected readonly retryReady = signal(true);
  private readonly attempted = signal<readonly RoleOperation[]>([]);
  protected readonly notApplied = computed(() => {
    const applied = new Set(this.store.editApplied().map((change) => this.operationKey(change)));
    return this.attempted().filter((change) => !applied.has(this.operationKey(change)));
  });
  private readonly diff = computed<readonly RoleOperation[]>(() => {
    const member = this.member();
    if (!member) return [];
    const current = this.store.memberById(member.id) ?? member;
    const currentIds = new Set(current.roles.map((role) => role.id));
    return [
      ...[...this.selected()]
        .filter((id) => !currentIds.has(id))
        .map((roleId): RoleOperation => ({ kind: 'grant', roleId })),
      ...current.roles
        .filter((role) => !this.selected().has(role.id))
        .map((role): RoleOperation => ({ kind: 'revoke', roleId: role.id })),
    ];
  });
  protected readonly canSave = computed(
    () =>
      !this.busy() &&
      this.retryReady() &&
      this.session.has('membership:write') &&
      this.store.roles().length > 0 &&
      this.diff().length > 0,
  );
  private readonly dialog = viewChild<unknown, ElementRef<HTMLElement>>('dialog', {
    read: ElementRef,
  });
  private readonly intents = new Map<string, Intent>();
  private readonly injector = inject(Injector);
  /** The edit phase the focus-restore effect saw last, so only a submitting -> failed move acts. */
  private lastEditPhase: MutationState['phase'] = 'idle';
  private opener: HTMLElement | null = null;
  private generation = 0;

  constructor() {
    inject(DestroyRef).onDestroy(() => this.finishClose());
    effect((onCleanup) => {
      const seconds = this.store.editState().error?.retryAfterSeconds;
      this.retryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.retryReady.set(true), seconds * 1000);
        onCleanup(() => clearTimeout(timer));
      }
    });
    // A refused save: the fieldset and Save were disabled for the request, so the control that had
    // focus lost it — put focus back on the first enabled role checkbox, the head of the dialog's
    // own tab order, once the re-enabled form has rendered.
    effect(() => {
      const phase = this.store.editState().phase;
      const wasSubmitting = this.lastEditPhase === 'submitting';
      this.lastEditPhase = phase;
      if (!wasSubmitting || phase !== 'failed' || !this.opened()) return;
      afterNextRender(
        () => {
          const first = this.dialog()?.nativeElement.querySelector<HTMLElement>(
            '[data-members-role-option] button:not([disabled])',
          );
          first?.focus();
        },
        { injector: this.injector },
      );
    });
  }

  open(member: MembershipView, opener: HTMLElement): void {
    if (
      !this.session.has('membership:write') ||
      (member.status !== 'active' && member.status !== 'pending') ||
      this.busy() ||
      this.store.inviteState().phase === 'submitting' ||
      this.store.removeState().phase === 'submitting'
    )
      return;
    this.generation++;
    this.opener = opener;
    this.member.set(member);
    this.selected.set(
      new Set((this.store.memberById(member.id) ?? member).roles.map((role) => role.id)),
    );
    this.intents.clear();
    this.attempted.set([]);
    this.store.resetMutations();
    this.opened.set(true);
  }

  /**
   * Closes the dialog and hands focus back to its opener. Programmatic on purpose: the page closes
   * it when the organization switches, and that must win over a save in flight — leaving it open
   * would show the old org's member over the newly loaded list.
   */
  close(): void {
    this.finishClose();
  }

  /**
   * The user's own cancel (footer Cancel). Refused while a save is in flight, so the attempt
   * finishes inside its own dialog and no reopened one can race it against the row it replaces.
   */
  protected requestClose(): void {
    if (this.busy()) return;
    this.close();
  }

  /** The close itself, also used when the component is destroyed and nothing can be in flight. */
  private finishClose(): void {
    this.generation++;
    this.opened.set(false);
    this.intents.clear();
    if (this.opener?.isConnected) this.opener.focus();
    this.opener = null;
  }

  /** Escape is a cancel only while no save is in flight; otherwise the dialog stays put. */
  protected cancel(event: Event): void {
    event.preventDefault();
    if (this.busy()) return;
    this.close();
  }

  protected toggle(roleId: string, checked: boolean): void {
    if (this.busy() || !this.session.has('membership:write')) return;
    const selected = new Set(this.selected());
    if (checked) selected.add(roleId);
    else selected.delete(roleId);
    this.selected.set(selected);
  }

  protected operationKey(change: RoleOperation): string {
    return `${change.kind}:${change.roleId}`;
  }

  protected roleName(roleId: string): string {
    return this.store.roles().find((role) => role.id === roleId)?.name ?? roleId;
  }

  protected async submit(): Promise<void> {
    const member = this.member();
    if (!this.opened() || !member || !this.canSave()) return;
    const changes = this.diff().map((change): RoleChange => {
      const key = this.operationKey(change);
      let intent = this.intents.get(key);
      if (!intent) {
        intent = createIntent();
        this.intents.set(key, intent);
      }
      return { ...change, intent };
    });
    this.attempted.set(changes);
    const generation = this.generation;
    const success = await this.store.changeRoles(member.id, changes);
    if (generation === this.generation) {
      const applied = new Set(this.store.editApplied().map((change) => this.operationKey(change)));
      if (!success && applied.size > 0) {
        // Rebase only the unapplied draft on the refetched row, never on optimistic changes.
        const current = this.store.memberById(member.id) ?? member;
        const selected = new Set(current.roles.map((role) => role.id));
        for (const change of this.notApplied()) {
          if (change.kind === 'grant') selected.add(change.roleId);
          else selected.delete(change.roleId);
        }
        this.selected.set(selected);
      }
      const remaining = new Set(this.diff().map((change) => this.operationKey(change)));
      for (const key of this.intents.keys()) {
        if (!remaining.has(key) || applied.has(key)) this.intents.delete(key);
      }
      if (success) this.close();
    }
    if (success) this.completed.emit();
  }
}
