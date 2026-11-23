import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  LOCALE_ID,
  output,
  signal,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmDropdownMenuImports } from '@evidgate/ui/dropdown-menu';
import type { MembershipView } from '@evidgate/sdk';
import {
  BADGE_CLASSES,
  BADGE_ICON_NAMES,
  BADGE_TONE_CLASSES,
} from '../../../shared/ui/chips/badge-classes';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import {
  DataTableComponent,
  type DataTableColumn,
} from '../../../shared/ui/data-table/data-table.component';
import type { DataTableCellContext } from '../../../shared/ui/data-table/data-table-column';
import { shortDateFormat } from '../../../shared/time/short-date-format';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { escapeFormatChars } from '../../../shared/ui/values/ref-name-display';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { memberRoleLabel } from './member-role-label';

export const MEMBERS_COPY = {
  title: $localize`:@@settings.members.title:Members and roles`,
  directory: $localize`:@@settings.members.directory:Members and invitations`,
  activeMembers: $localize`:@@settings.members-active-count:Active members`,
  pendingInvitations: $localize`:@@settings.members-pending-count:Pending invitations`,
  organizationAdmins: $localize`:@@settings.members-admin-count:Organization admins`,
  loading: $localize`:@@settings.members.loading:Loading members…`,
  description: $localize`:@@settings.members.description:Review organization roles and invitations before changing membership.`,
  member: $localize`:@@settings.members.member:Member`,
  status: $localize`:@@settings.members.status:Status`,
  roles: $localize`:@@settings.members.roles:Organization roles`,
  actions: $localize`:@@settings.members.actions:Actions`,
  remove: $localize`:@@settings.members.remove:Remove member`,
  invite: $localize`:@@settings.members.invite:Invite member`,
  cancel: $localize`:@@settings.members.cancel:Cancel`,
  email: $localize`:@@settings.members.email:Email`,
  emailRequired: $localize`:@@settings.members.email-required:Enter an email address.`,
  emailInvalid: $localize`:@@settings.members.email-invalid:Enter a valid email address.`,
  emailTooLong: $localize`:@@settings.members.email-too-long:Use an email address with 320 characters or fewer.`,
  optionalRoles: $localize`:@@settings.members.optional-roles:Initial roles (optional)`,
  send: $localize`:@@settings.members.send:Send invitation`,
  resend: $localize`:@@settings.members.resend:Resend invite`,
  cancelInvite: $localize`:@@settings.members.cancel-invite:Cancel invite`,
  resendTitle: $localize`:@@settings.members.resend-title:Renew this invitation?`,
  resendDescription: $localize`:@@settings.members.resend-description:The previous invite token will stop working. Copy the new token and share it with the invitee securely.`,
  cancelInviteTitle: $localize`:@@settings.members.cancel-invite-title:Cancel this invitation?`,
  cancelInviteDescription: $localize`:@@settings.members.cancel-invite-description:The invite token will stop working. This person will not be able to accept the invitation.`,
  revealTitle: $localize`:@@settings.members.reveal-title:Invite token`,
  revealWarning: $localize`:@@settings.members.reveal-warning:Shown once. Copy this token now and share it securely with the invitee, who needs it to accept. If Copy fails, select and copy the value manually.`,
  replayTitle: $localize`:@@settings.members.replay-title:Invitation ready`,
  replayNotice: $localize`:@@settings.members.replay-notice:The invitation was created or renewed, but its token can no longer be shown. Use Resend on the member row to get a new one.`,
  copy: $localize`:@@settings.members.copy:Copy`,
  copied: $localize`:@@settings.members.copied:Copied`,
  dismiss: $localize`:@@settings.members.dismiss:Dismiss`,
  close: $localize`:@@settings.members.close:Close`,
  submitting: $localize`:@@settings.members.submitting:Submitting…`,
  removeTitle: $localize`:@@settings.members.remove-title:Remove this member?`,
  removeDescription: $localize`:@@settings.members.remove-description:Organization access will be revoked. Authored history stays attributed.`,
  inviteRefused: $localize`:@@settings.members.invite-refused:Invitation not sent`,
  removeRefused: $localize`:@@settings.members.remove-refused:Member not removed`,
  correctInvitation: $localize`:@@settings.members.correct-invitation:Correct the email or roles to start a new invitation request.`,
  fixedInvitation: $localize`:@@settings.members.fixed-invitation:Email and roles are fixed for this attempt. Retry uses the same request. Close this dialog to start a new attempt.`,
  restartInvitation: $localize`:@@settings.members.restart-invitation:This attempt cannot be changed or sent again. Close this dialog to start a new attempt.`,
  editRoles: $localize`:@@settings.members.edit-roles:Edit roles`,
  editTitle: $localize`:@@settings.members.edit-title:Edit organization roles`,
  editDescription: $localize`:@@settings.members.edit-description:Select the organization roles for this member.`,
  editRolesLegend: $localize`:@@settings.members.edit-roles-legend:Organization roles for this member`,
  save: $localize`:@@settings.members.save:Save changes`,
  saving: $localize`:@@settings.members.saving:Saving…`,
  lastAdminHint: $localize`:@@settings.members.last-admin-hint:The server refuses to demote the last organization admin. Grant tenant_admin to another member first.`,
  rolesUnavailableEdit: $localize`:@@settings.members.roles-unavailable-edit:Organization roles are unavailable. Try again when the role catalogue is available.`,
  applied: $localize`:@@settings.members.applied:Applied changes`,
  notApplied: $localize`:@@settings.members.not-applied:Not applied`,
  roleGranted: $localize`:@@settings.members.role-granted:Grant role`,
  roleRevoked: $localize`:@@settings.members.role-revoked:Revoke role`,
  partialCurrent: $localize`:@@settings.members.partial-current:The list now shows the server's current roles. The remaining changes have not been applied.`,
  matrix: $localize`:@@settings.members.matrix:Organization-wide role matrix`,
  granted: $localize`:@@settings.members.granted:Granted`,
  notGranted: $localize`:@@settings.members.not-granted:Not granted`,
  noRoles: $localize`:@@settings.members.no-roles:No roles assigned`,
  rolesUnavailable: $localize`:@@settings.members.roles-unavailable:Role catalogue unavailable. Memberships remain visible.`,
  preview: $localize`:@@settings.members.preview:Role scope preview`,
  unavailable: $localize`:@@settings.members.preview-unavailable:Preview unavailable for this role`,
  authority: $localize`:@@settings.members.authority:Display only. The server decides actual access.`,
  sensitive: $localize`:@@settings.members.sensitive:Sensitive`,
  omitted: $localize`:@@settings.members.omitted:Permissions omitted because they are missing from the live catalogue:`,
  emptyTitle: $localize`:@@settings.members.empty-title:No members yet`,
  emptyDescription: $localize`:@@settings.members.empty-description:Members and pending invitations will appear here.`,
  next: $localize`:@@settings.members.next:Next members`,
  previous: $localize`:@@settings.members.previous:Previous members`,
  saved: $localize`:@@settings.members.saved:Membership updated.`,
  loadMoreFailed: $localize`:@@settings.members.load-more-failed:The next members page could not be loaded. The members already shown are unchanged.`,
  narrow: $localize`:@@settings.members.narrow:Use a wider screen to invite, edit or remove members.`,
  pending: $localize`:@@settings.members.pending:Invitation pending`,
  active: $localize`:@@settings.members.active:Active`,
  removed: $localize`:@@settings.members.removed:Removed`,
  revoked: $localize`:@@settings.members.revoked:Revoked`,
  expired: $localize`:@@settings.members.expired:Expired`,
};

@Component({
  selector: 'app-member-list',
  imports: [
    StatusPillComponent,
    DataTableComponent,
    DatePipe,
    AbsoluteTimeComponent,
    ShellIconComponent,
    HlmButton,
    HlmBadge,
    HlmIcon,
    HlmDialogImports,
    HlmDropdownMenuImports,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <ng-template #identityCell let-member let-stacked="stacked">
      <div class="flex min-w-0 items-center gap-3">
        <span
          class="grid size-8 shrink-0 place-items-center rounded-full bg-secondary text-muted-foreground"
        >
          <app-shell-icon [name]="member.status === 'pending' ? 'inbox' : 'account'" />
        </span>
        <div class="grid min-w-0 flex-1 gap-1">
          <button
            #emailButton
            hlmBtn
            [variant]="'link'"
            type="button"
            data-member-view
            [attr.aria-haspopup]="'dialog'"
            class="h-auto max-w-full justify-start px-0 py-1 text-start font-semibold pointer-coarse:min-h-(--eg-control-touch)"
            (click)="inspection.set({ id: member.id, trigger: emailButton })"
          >
            <span class="sr-only" i18n="@@settings.members.view">View member</span>
            <bdi [class.truncate]="!stacked" [class.whitespace-normal]="stacked">{{
              display(member.invited_email)
            }}</bdi>
          </button>
          <small class="text-xs text-muted-foreground">
            <span i18n="@@settings.members.created-date"
              >Created {{ member.created_at | date: calendarFormat : 'UTC' }} UTC</span
            >
          </small>
        </div>
      </div>
    </ng-template>
    <ng-template #statusCell let-member>
      <span
        data-members-row
        [attr.data-membership-id]="member.id"
        [attr.data-members-status]="member.status"
      >
        @if (member.status === 'pending') {
          <span hlmBadge [variant]="'secondary'" [class]="pendingBadge">
            <hlm-icon [name]="pendingIcon" />
            <span>{{ copy.pending }}</span>
          </span>
        } @else {
          <app-status-pill
            [tone]="member.status === 'active' ? 'info' : 'neutral'"
            [label]="statusLabel(member.status)"
          />
        }
      </span>
    </ng-template>
    <ng-template #rolesCell let-member let-stacked="stacked">
      <div class="flex min-w-0 items-center gap-2" [class.flex-wrap]="stacked">
        @for (role of member.roles.slice(0, 2); track role.id) {
          <span hlmBadge [variant]="'secondary'" class="role-name min-w-0">{{
            display(roleLabel(role))
          }}</span>
        } @empty {
          {{ copy.noRoles }}
        }
        @if (member.roles.length > 2) {
          <hlm-dialog>
            <button
              hlmBtn
              hlmDialogTrigger
              [variant]="'ghost'"
              type="button"
              class="shrink-0 min-h-[var(--eg-control-touch)]"
              [attr.aria-label]="moreRoles(member)"
              data-members-more-roles
            >
              +{{ member.roles.length - 2 }}
            </button>
            <hlm-dialog-content *hlmDialogPortal [size]="'md'" [closeLabel]="copy.cancel">
              <header hlmDialogHeader>
                <h2 hlmDialogTitle>{{ copy.roles }}</h2>
                <p hlmDialogDescription class="m-0 wrap-anywhere">
                  {{ display(member.invited_email) }}
                </p>
              </header>
              <ul class="m-0 grid min-w-0 gap-2 ps-4">
                @for (role of member.roles; track role.id) {
                  <li class="wrap-anywhere">{{ display(roleLabel(role)) }}</li>
                }
              </ul>
              <footer hlmDialogFooter>
                <button hlmBtn hlmDialogClose [variant]="'outline'" type="button">
                  {{ copy.cancel }}
                </button>
              </footer>
            </hlm-dialog-content>
          </hlm-dialog>
        }
      </div>
    </ng-template>
    <ng-template #actionsCell let-member>
      <button
        #viewButton
        hlmBtn
        [variant]="'outline'"
        [size]="'sm'"
        type="button"
        data-member-row-view
        class="pointer-coarse:min-h-(--eg-control-touch) pointer-coarse:min-w-(--eg-control-touch)"
        [attr.aria-haspopup]="'dialog'"
        [attr.aria-label]="view + ' · ' + display(member.invited_email)"
        (click)="inspection.set({ id: member.id, trigger: viewButton })"
      >
        {{ view }}
      </button>
      @if (canWrite() && !viewport.narrow() && member.status !== 'removed') {
        <button
          hlmBtn
          #actionsButton
          [variant]="'ghost'"
          type="button"
          [size]="'icon-sm'"
          class="mutation ms-2 min-h-(--eg-control-sm) min-w-max whitespace-nowrap pointer-coarse:min-h-(--eg-control-touch)"
          [hlmDropdownMenuTrigger]="actionsMenu"
          data-members-actions
          [attr.data-members-status]="member.status"
          [attr.data-membership-id]="member.id"
        >
          <span aria-hidden="true" class="text-lg leading-none">⋯</span>
          <span class="sr-only">{{ copy.actions }} {{ member.invited_email }}</span>
        </button>
        <ng-template #actionsMenu>
          <hlm-dropdown-menu
            class="min-w-max [&_button]:justify-start [&_button]:text-start [&_button]:whitespace-nowrap"
          >
            @if (member.status === 'active' || member.status === 'pending') {
              <button
                hlmDropdownMenuItem
                type="button"
                data-members-edit-open
                (triggered)="requestEditRoles(member, actionsButton)"
              >
                {{ copy.editRoles }}
              </button>
            }
            @if (member.status === 'pending') {
              <button
                hlmDropdownMenuItem
                type="button"
                data-members-resend-open
                (triggered)="requestResend(member, actionsButton)"
              >
                {{ copy.resend }}
              </button>
              <button
                hlmDropdownMenuItem
                type="button"
                data-members-cancel-invite-open
                (triggered)="requestCancelInvite(member, actionsButton)"
              >
                {{ copy.cancelInvite }}
              </button>
            }
            <button
              hlmDropdownMenuItem
              [variant]="'destructive'"
              type="button"
              data-members-remove
              (triggered)="requestRemove(member, actionsButton)"
            >
              {{ copy.remove }}
            </button>
          </hlm-dropdown-menu>
        </ng-template>
      }
    </ng-template>
    <app-data-table
      class="[&_tbody_td]:align-middle [&_[data-column-role=actions]]:whitespace-nowrap [&_[data-column-role=actions]]:wrap-normal"
      [flow]="'page'"
      [columns]="columns()"
      [rows]="members()"
      [rowId]="rowId"
      [caption]="copy.directory"
    />
    @if (inspection(); as selection) {
      <hlm-dialog
        [aria-modal]="true"
        [state]="inspectedMember() ? 'open' : 'closed'"
        [autoFocus]="'first-heading'"
        [restoreFocus]="selection.trigger"
        (closed)="inspection.set(null)"
      >
        <hlm-dialog-content *hlmDialogPortal [showCloseButton]="false" [size]="'md'">
          <header hlmDialogHeader>
            <h2 hlmDialogTitle>{{ copy.member }}</h2>
            <p hlmDialogDescription>{{ copy.directory }}</p>
          </header>
          @if (inspectedMember(); as member) {
            <dl class="hlm-field-grid [&_dt]:text-xs">
              <div class="hlm-field-wide">
                <dt>{{ copy.email }}</dt>
                <dd>
                  <bdi>{{ display(member.invited_email) }}</bdi>
                </dd>
              </div>
              <div>
                <dt>{{ copy.status }}</dt>
                <dd>{{ statusLabel(member.status) }}</dd>
              </div>
              <div>
                <dt i18n="@@settings.members.created">Created</dt>
                <dd><app-absolute-time [value]="member.created_at" /></dd>
              </div>
              <div class="hlm-field-wide">
                <dt>{{ copy.roles }}</dt>
                <dd>
                  @if (member.roles.length) {
                    <ul class="m-0 list-none p-0">
                      @for (role of member.roles; track role.id) {
                        <li>
                          <bdi>{{ display(roleLabel(role)) }}</bdi>
                        </li>
                      }
                    </ul>
                  } @else {
                    {{ copy.noRoles }}
                  }
                </dd>
              </div>
            </dl>
          }
          <footer hlmDialogFooter>
            <button hlmBtn hlmDialogClose [variant]="'outline'" type="button" i18n="@@ui.close">
              Close
            </button>
          </footer>
        </hlm-dialog-content>
      </hlm-dialog>
    }
  `,
})
export class MemberListComponent {
  protected readonly viewport = inject(NarrowMutationPolicy);
  protected readonly calendarFormat = shortDateFormat(inject(LOCALE_ID));
  protected readonly display = escapeFormatChars;
  protected readonly roleLabel = memberRoleLabel;
  protected readonly view = $localize`:@@settings.row.view:View`;

  readonly members = input.required<readonly MembershipView[]>();
  readonly canWrite = input(false);
  protected readonly inspection = signal<{ id: string; trigger: HTMLButtonElement } | null>(null);
  protected readonly inspectedMember = computed(() => {
    const selected = this.inspection();
    return selected ? this.members().find((member) => member.id === selected.id) : undefined;
  });
  readonly remove = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly editRoles = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly resend = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly cancelInvite = output<{ member: MembershipView; opener: HTMLElement }>();
  protected readonly copy = MEMBERS_COPY;
  protected readonly pendingBadge = `${BADGE_CLASSES} ${BADGE_TONE_CLASSES.pending}`;
  protected readonly pendingIcon = BADGE_ICON_NAMES.pending;
  private readonly identityCell =
    viewChild<TemplateRef<DataTableCellContext<MembershipView>>>('identityCell');
  private readonly statusCell =
    viewChild<TemplateRef<DataTableCellContext<MembershipView>>>('statusCell');
  private readonly rolesCell =
    viewChild<TemplateRef<DataTableCellContext<MembershipView>>>('rolesCell');
  private readonly actionsCell =
    viewChild<TemplateRef<DataTableCellContext<MembershipView>>>('actionsCell');
  protected readonly rowId = (member: MembershipView): string => member.id;
  protected readonly columns = computed<readonly DataTableColumn<MembershipView>[]>(() => {
    const identity = this.identityCell();
    const status = this.statusCell();
    const roles = this.rolesCell();
    const actions = this.actionsCell();
    if (!identity || !status || !roles || !actions) return [];
    return [
      {
        id: 'member',
        header: this.copy.member,
        role: 'identity',
        kind: 'email',
        priority: 'primary',
        overflow: 'truncate',
        cell: (member) => member.invited_email,
        cellTemplate: identity,
      },
      {
        id: 'roles',
        header: this.copy.roles,
        role: 'text',
        overflow: 'truncate',
        cell: () => this.copy.roles,
        cellTemplate: roles,
      },
      {
        id: 'status',
        header: this.copy.status,
        role: 'status',
        cell: (member) => this.statusLabel(member.status),
        cellTemplate: status,
      },
      {
        id: 'actions',
        header: this.copy.actions,
        role: 'actions',
        width: { min: 'max-content' },
        cell: () => this.copy.actions,
        cellTemplate: actions,
      },
    ];
  });
  protected moreRoles(member: MembershipView): string {
    const count = member.roles.length - 2;
    return $localize`:@@settings.members.more-roles:Show ${count}:count: more roles for ${member.invited_email}:email:`;
  }
  protected requestRemove(member: MembershipView, opener: HTMLElement): void {
    if (this.viewport.allowsMutation() && this.canWrite() && member.status !== 'removed')
      this.remove.emit({ member, opener });
  }
  protected requestEditRoles(member: MembershipView, opener: HTMLElement): void {
    if (
      this.viewport.allowsMutation() &&
      this.canWrite() &&
      (member.status === 'active' || member.status === 'pending')
    )
      this.editRoles.emit({ member, opener });
  }
  protected requestResend(member: MembershipView, opener: HTMLElement): void {
    if (this.viewport.allowsMutation() && this.canWrite() && member.status === 'pending')
      this.resend.emit({ member, opener });
  }
  protected requestCancelInvite(member: MembershipView, opener: HTMLElement): void {
    if (this.viewport.allowsMutation() && this.canWrite() && member.status === 'pending')
      this.cancelInvite.emit({ member, opener });
  }

  // Unknown wire statuses stay literal, never an inherited property of the copy catalogue.
  protected statusLabel(status: string): string {
    switch (status) {
      case 'pending':
        return this.copy.pending;
      case 'active':
        return this.copy.active;
      case 'removed':
        return this.copy.removed;
      case 'revoked':
        return this.copy.revoked;
      case 'expired':
        return this.copy.expired;
      default:
        return status;
    }
  }
}
