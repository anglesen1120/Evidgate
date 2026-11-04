import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import { MEMBERS_COPY } from './member-list.component';

@Component({
  selector: 'app-role-matrix',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './members.page.css',
  template: `
    @if (matrix().columns.length) {
      <div class="table-scroll" tabindex="0" role="region" [attr.aria-label]="copy.matrix">
        <table data-members-matrix>
          <caption>
            {{
              copy.matrix
            }}
          </caption>
          <thead>
            <tr>
              <th scope="col">{{ copy.member }}</th>
              @for (role of matrix().columns; track role.id) {
                <th scope="col" [id]="'matrix-role-' + role.id">{{ role.name }}</th>
              }
            </tr>
          </thead>
          <tbody>
            @for (row of matrix().rows; track row.membership.id) {
              <tr>
                <th scope="row" class="member-identity" [id]="'matrix-member-' + row.membership.id">
                  <span class="record-label" aria-hidden="true">{{ copy.member }}</span>
                  <span>{{ row.membership.invited_email }}</span>
                </th>
                @for (role of matrix().columns; track role.id) {
                  <td
                    class="role-grant"
                    [attr.headers]="
                      'matrix-member-' + row.membership.id + ' matrix-role-' + role.id
                    "
                  >
                    <span class="record-label" aria-hidden="true">{{ role.name }}</span>
                    <span [attr.data-granted]="row.granted.has(role.id) ? 'true' : null">
                      {{ row.granted.has(role.id) ? copy.granted : copy.notGranted }}
                    </span>
                  </td>
                }
              </tr>
            }
          </tbody>
        </table>
      </div>
    } @else {
      <p role="status">{{ copy.rolesUnavailable }}</p>
    }
  `,
})
export class RoleMatrixComponent {
  readonly matrix = input.required<{
    columns: readonly RoleView[];
    rows: readonly { membership: MembershipView; granted: ReadonlySet<string> }[];
  }>();
  protected readonly copy = MEMBERS_COPY;
}
