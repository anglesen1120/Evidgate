import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { HlmTableImports } from '@evidgate/ui/table';
import { HlmButton } from '@evidgate/ui/button';
import { HlmBadge } from '@evidgate/ui/badge';
import type { MembershipView } from '@evidgate/sdk';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';

export const MEMBERS_COPY = {
  title: $localize`:@@settings.members.title:Members and roles`,
  directory: $localize`:@@settings.members.directory:Members and invitations`,
  description: $localize`:@@settings.members.description:Manage organization membership. The server decides actual access.`,
  member: $localize`:@@settings.members.member:Member`,
  status: $localize`:@@settings.members.status:Status`,
  roles: $localize`:@@settings.members.roles:Organization roles`,
  actions: $localize`:@@settings.members.actions:Actions`,
  remove: $localize`:@@settings.members.remove:Remove member`,
  invite: $localize`:@@settings.members.invite:Invite member`,
  cancel: $localize`:@@settings.members.cancel:Cancel`,
  email: $localize`:@@settings.members.email:Email`,
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
  pending: $localize`:@@settings.members.pending:Pending invitation`,
  active: $localize`:@@settings.members.active:Active`,
  removed: $localize`:@@settings.members.removed:Removed`,
  revoked: $localize`:@@settings.members.revoked:Revoked`,
  expired: $localize`:@@settings.members.expired:Expired`,
};

@Component({
  selector: 'app-member-list',
  imports: [StatusPillComponent, HlmTableImports, HlmButton, HlmBadge],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <div
      hlmTableContainer
      class="table-scroll min-w-0 max-w-full rounded-md border border-solid border-border bg-card max-md:overflow-visible focus-visible:outline-[var(--eg-focus-width)] focus-visible:outline-ring focus-visible:outline-offset-[var(--eg-focus-offset)]"
      tabindex="0"
      role="region"
      [attr.aria-label]="copy.directory"
    >
      <table
        hlmTable
        role="table"
        class="w-full table-fixed caption-top border-collapse max-md:block [&_tbody]:max-md:block [&_tr]:max-md:block [&_thead]:max-md:sr-only [&_th]:break-normal [&_td]:break-normal [&_th]:whitespace-normal [&_td]:whitespace-normal [&_tbody_th]:wrap-anywhere [&_tbody_th]:max-md:grid [&_tbody_td]:max-md:grid [&_tbody_th]:max-md:gap-1 [&_tbody_td]:max-md:gap-1 [&_tbody_th]:max-md:min-h-[var(--eg-control-touch)] [&_tbody_td]:max-md:min-h-[var(--eg-control-touch)] [&_tbody_tr]:max-md:border-t"
      >
        <caption hlmCaption class="m-0 p-3 text-start font-semibold text-foreground max-md:block">
          {{
            copy.directory
          }}
        </caption>
        <thead hlmTHead role="rowgroup" class="bg-secondary">
          <tr hlmTr role="row">
            <th hlmTh role="columnheader" scope="col" id="members-member" class="w-1/4">
              {{ copy.member }}
            </th>
            <th hlmTh role="columnheader" scope="col" id="members-status" class="w-1/6">
              {{ copy.status }}
            </th>
            <th hlmTh role="columnheader" scope="col" id="members-roles" class="w-1/4">
              {{ copy.roles }}
            </th>
            @if (canWrite()) {
              <th
                hlmTh
                role="columnheader"
                scope="col"
                class="mutation max-md:hidden! md:table-cell"
              >
                {{ copy.actions }}
              </th>
            }
          </tr>
        </thead>
        <tbody hlmTBody role="rowgroup">
          @for (member of members(); track member.id) {
            <tr
              hlmTr
              role="row"
              data-members-row
              [attr.data-membership-id]="member.id"
              [attr.data-members-status]="member.status"
            >
              <th
                hlmTh
                role="rowheader"
                scope="row"
                class="member-identity px-3 py-2"
                [id]="'member-' + member.id"
              >
                <span
                  class="record-label block text-xs font-medium text-muted-foreground whitespace-normal md:hidden"
                  >{{ copy.member }}</span
                >
                <span>{{ member.invited_email }}</span>
              </th>
              <td hlmTd role="cell" [attr.headers]="'member-' + member.id + ' members-status'">
                <span
                  class="record-label block text-xs font-medium text-muted-foreground whitespace-normal md:hidden"
                  >{{ copy.status }}</span
                >
                <app-status-pill
                  [tone]="member.status === 'active' ? 'success' : 'neutral'"
                  [label]="statusLabel(member.status)"
                />
              </td>
              <td hlmTd role="cell" [attr.headers]="'member-' + member.id + ' members-roles'">
                <span
                  class="record-label block text-xs font-medium text-muted-foreground whitespace-normal md:hidden"
                  >{{ copy.roles }}</span
                >
                <span class="flex min-w-0 flex-wrap items-center gap-2">
                  @for (role of member.roles; track role.id) {
                    <span
                      hlmBadge
                      [variant]="'secondary'"
                      class="role-name max-w-full whitespace-normal wrap-anywhere"
                      >{{ role.name }}</span
                    >
                  } @empty {
                    {{ copy.noRoles }}
                  }
                </span>
              </td>
              @if (canWrite()) {
                <td hlmTd role="cell" class="mutation max-md:hidden! md:table-cell">
                  <div class="flex min-w-0 flex-wrap items-center gap-2">
                    @if (member.status === 'active' || member.status === 'pending') {
                      <button
                        hlmBtn
                        [variant]="'outline'"
                        class="h-auto min-h-[var(--eg-control-touch)] max-w-full whitespace-normal wrap-anywhere"
                        type="button"
                        data-members-edit-open
                        (click)="requestEditRoles(member, $event)"
                      >
                        {{ copy.editRoles }}
                      </button>
                    }
                    @if (member.status === 'pending') {
                      <button
                        hlmBtn
                        [variant]="'outline'"
                        class="h-auto min-h-[var(--eg-control-touch)] max-w-full whitespace-normal wrap-anywhere"
                        type="button"
                        data-members-resend-open
                        (click)="requestResend(member, $event)"
                      >
                        {{ copy.resend }}
                      </button>
                      <button
                        hlmBtn
                        [variant]="'outline'"
                        class="h-auto min-h-[var(--eg-control-touch)] max-w-full whitespace-normal wrap-anywhere"
                        type="button"
                        data-members-cancel-invite-open
                        (click)="requestCancelInvite(member, $event)"
                      >
                        {{ copy.cancelInvite }}
                      </button>
                    }
                    @if (member.status !== 'removed') {
                      <button
                        hlmBtn
                        [variant]="'destructive'"
                        class="ms-3 h-auto min-h-[var(--eg-control-touch)] max-w-full whitespace-normal wrap-anywhere"
                        type="button"
                        data-members-remove
                        (click)="requestRemove(member, $event)"
                      >
                        {{ copy.remove }}
                      </button>
                    }
                  </div>
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class MemberListComponent {
  readonly members = input.required<readonly MembershipView[]>();
  readonly canWrite = input(false);
  readonly remove = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly editRoles = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly resend = output<{ member: MembershipView; opener: HTMLElement }>();
  readonly cancelInvite = output<{ member: MembershipView; opener: HTMLElement }>();
  protected readonly copy = MEMBERS_COPY;
  protected requestRemove(member: MembershipView, event: Event): void {
    if (event.currentTarget instanceof HTMLElement)
      this.remove.emit({ member, opener: event.currentTarget });
  }
  protected requestEditRoles(member: MembershipView, event: Event): void {
    if (event.currentTarget instanceof HTMLElement)
      this.editRoles.emit({ member, opener: event.currentTarget });
  }
  protected requestResend(member: MembershipView, event: Event): void {
    if (
      this.canWrite() &&
      member.status === 'pending' &&
      event.currentTarget instanceof HTMLElement
    )
      this.resend.emit({ member, opener: event.currentTarget });
  }
  protected requestCancelInvite(member: MembershipView, event: Event): void {
    if (
      this.canWrite() &&
      member.status === 'pending' &&
      event.currentTarget instanceof HTMLElement
    )
      this.cancelInvite.emit({ member, opener: event.currentTarget });
  }

  /**
   * The label of one membership status: a closed switch over the five contract values
   * (`MembershipView['status']`), never an object keyed by server data — `copy[status]` would
   * answer a prototype member such as `constructor` or the inherited `toString` for a status the
   * contract does not define. The parameter is widened to `string` because the value arrives from
   * the wire, so the fallback branch is genuinely reachable: an unknown status is shown as the
   * server wrote it, as plain text, rather than as an invented or empty label.
   */
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
