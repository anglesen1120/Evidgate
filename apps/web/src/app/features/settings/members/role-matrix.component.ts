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
                <th scope="col">{{ role.name }}</th>
              }
            </tr>
          </thead>
          <tbody>
            @for (row of matrix().rows; track row.membership.id) {
              <tr>
                <th scope="row">{{ row.membership.invited_email }}</th>
                @for (role of matrix().columns; track role.id) {
                  <td [attr.data-granted]="row.granted.has(role.id) ? 'true' : null">
                    {{ row.granted.has(role.id) ? copy.granted : copy.notGranted }}
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
