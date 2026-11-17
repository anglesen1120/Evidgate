import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  type TemplateRef,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmDropdownMenuImports } from '@evidgate/ui/dropdown-menu';
import type { MembershipView } from '@evidgate/sdk';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import {
  DataTableComponent,
  type DataTableColumn,
} from '../../../shared/ui/data-table/data-table.component';
import type { DataTableCellContext } from '../../../shared/ui/data-table/data-table-column';

export const MEMBERS_COPY = {
  title: $localize`:@@settings.members.title:Members and roles`,
  directory: $localize`:@@settings.members.directory:Members and invitations`,
  loading: $localize`:@@settings.members.loading:Loading members…`,
  description: $localize`:@@settings.members.description:Manage organization membership. The server decides actual access.`,
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
  pending: $localize`:@@settings.members.pending:Invited`,
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
    HlmButton,
    HlmBadge,
    HlmDialogImports,
    HlmDropdownMenuImports,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <ng-template #statusCell let-member>
      <span
        data-members-row
        [attr.data-membership-id]="member.id"
        [attr.data-members-status]="member.status"
      >
        <app-status-pill
          [tone]="member.status === 'active' ? 'success' : 'neutral'"
          [label]="statusLabel(member.status)"
        />
      </span>
    </ng-template>
    <ng-template #rolesCell let-member let-stacked="stacked">
      <div class="flex min-w-0 items-center gap-2" [class.flex-wrap]="stacked">
        @for (role of member.roles.slice(0, 2); track role.id) {
          <span hlmBadge [variant]="'secondary'" class="role-name min-w-0">{{ role.name }}</span>
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
            <hlm-dialog-content *hlmDialogPortal [closeLabel]="copy.cancel">
              <header hlmDialogHeader>
                <h2 hlmDialogTitle>{{ copy.roles }}</h2>
                <p hlmDialogDescription class="m-0 wrap-anywhere">{{ member.invited_email }}</p>
              </header>
              <ul class="m-0 grid min-w-0 gap-2 ps-4">
                @for (role of member.roles; track role.id) {
                  <li class="wrap-anywhere">{{ role.name }}</li>
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
      @if (member.status !== 'removed') {
        <button
          hlmBtn
          #actionsButton
          [variant]="'ghost'"
          type="button"
          class="mutation min-h-[var(--eg-control-touch)]"
          [hlmDropdownMenuTrigger]="actionsMenu"
          data-members-actions
          [attr.data-members-status]="member.status"
          [attr.data-membership-id]="member.id"
        >
          {{ copy.actions }}<span class="sr-only"> {{ member.invited_email }}</span>
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
      [columns]="columns()"
      [rows]="members()"
      [rowId]="rowId"
      [caption]="copy.directory"
    />
  `,
})
export class MemberListComponent {
  protected readonly viewport = inject(NarrowMutationPolicy);

  readonly members = input.required<readonly MembershipView[]>();
  readonly canWrite = input(false);
  readonly remove = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly editRoles = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly resend = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly cancelInvite = output<{ member: MembershipView; opener: HTMLElement }>();
  protected readonly copy = MEMBERS_COPY;
  private readonly statusCell =
    viewChild<TemplateRef<DataTableCellContext<MembershipView>>>('statusCell');
  private readonly rolesCell =
    viewChild<TemplateRef<DataTableCellContext<MembershipView>>>('rolesCell');
  private readonly actionsCell =
    viewChild<TemplateRef<DataTableCellContext<MembershipView>>>('actionsCell');
  protected readonly rowId = (member: MembershipView): string => member.id;
  protected readonly columns = computed<readonly DataTableColumn<MembershipView>[]>(() => {
    const status = this.statusCell();
    const roles = this.rolesCell();
    const actions = this.actionsCell();
    if (!status || !roles || !actions) return [];
    const columns: DataTableColumn<MembershipView>[] = [
      {
        id: 'member',
        header: this.copy.member,
        role: 'identity',
        kind: 'email',
        priority: 'primary',
        overflow: 'truncate',
        cell: (member) => member.invited_email,
      },
      {
        id: 'status',
        header: this.copy.status,
        role: 'status',
        cell: (member) => this.statusLabel(member.status),
        cellTemplate: status,
      },
      {
        id: 'roles',
        header: this.copy.roles,
        role: 'text',
        overflow: 'truncate',
        cell: () => this.copy.roles,
        cellTemplate: roles,
      },
    ];
    if (this.canWrite()) {
      columns.push({
        id: 'actions',
        header: this.copy.actions,
        role: 'actions',
        mutates: true,
        cell: () => this.copy.actions,
        cellTemplate: actions,
      });
    }
    return columns;
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
