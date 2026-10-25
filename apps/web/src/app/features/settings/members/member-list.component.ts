import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
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
  submitting: $localize`:@@settings.members.submitting:Submitting…`,
  removeTitle: $localize`:@@settings.members.remove-title:Remove this member?`,
  removeDescription: $localize`:@@settings.members.remove-description:Organization access will be revoked. Authored history stays attributed.`,
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
  narrow: $localize`:@@settings.members.narrow:Use a wider screen to invite or remove members.`,
  pending: $localize`:@@settings.members.pending:Pending invitation`,
  active: $localize`:@@settings.members.active:Active`,
  removed: $localize`:@@settings.members.removed:Removed`,
  revoked: $localize`:@@settings.members.revoked:Revoked`,
  expired: $localize`:@@settings.members.expired:Expired`,
};

@Component({
  selector: 'app-member-list',
  imports: [StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './members.page.css',
  template: `
    <div class="table-scroll" tabindex="0" role="region" [attr.aria-label]="copy.directory">
      <table>
        <caption>
          {{
            copy.directory
          }}
        </caption>
        <thead>
          <tr>
            <th scope="col">{{ copy.member }}</th>
            <th scope="col">{{ copy.status }}</th>
            <th scope="col">{{ copy.roles }}</th>
            @if (canWrite()) {
              <th scope="col" class="mutation">{{ copy.actions }}</th>
            }
          </tr>
        </thead>
        <tbody>
          @for (member of members(); track member.id) {
            <tr
              data-members-row
              [attr.data-membership-id]="member.id"
              [attr.data-members-status]="member.status"
            >
              <th scope="row">{{ member.invited_email }}</th>
              <td>
                <app-status-pill
                  [tone]="member.status === 'active' ? 'success' : 'neutral'"
                  [label]="statusLabel(member.status)"
                />
              </td>
              <td>
                @for (role of member.roles; track role.id) {
                  <span class="role-name">{{ role.name }}</span>
                } @empty {
                  {{ copy.noRoles }}
                }
              </td>
              @if (canWrite()) {
                <td class="mutation">
                  @if (member.status !== 'removed') {
                    <button
                      type="button"
                      data-members-remove
                      (click)="requestRemove(member, $event)"
                    >
                      {{ copy.remove }}
                    </button>
                  }
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
  protected readonly copy = MEMBERS_COPY;
  protected requestRemove(member: MembershipView, event: Event): void {
    if (event.currentTarget instanceof HTMLElement)
      this.remove.emit({ member, opener: event.currentTarget });
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
