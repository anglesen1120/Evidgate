import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import { HlmTableImports } from '@evidgate/ui/table';
import { MEMBERS_COPY } from './member-list.component';

@Component({
  selector: 'app-role-matrix',
  imports: [HlmTableImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    @if (matrix().columns.length) {
      <div
        hlmTableContainer
        class="table-scroll min-w-0 max-w-full rounded-md border border-solid border-border bg-card max-md:overflow-visible focus-visible:outline-[var(--eg-focus-width)] focus-visible:outline-ring focus-visible:outline-offset-[var(--eg-focus-offset)]"
        tabindex="0"
        role="region"
        [attr.aria-label]="copy.matrix"
      >
        <table
          hlmTable
          class="caption-top border-collapse max-md:block [&_tbody]:max-md:block [&_tr]:max-md:block [&_thead]:max-md:sr-only [&_th]:break-normal [&_td]:break-normal [&_tbody_th]:max-md:grid [&_tbody_td]:max-md:grid [&_tbody_th]:max-md:gap-1 [&_tbody_td]:max-md:gap-1 [&_tbody_th]:max-md:whitespace-normal [&_tbody_td]:max-md:whitespace-normal [&_tbody_th]:max-md:wrap-anywhere [&_tbody_td]:max-md:wrap-anywhere [&_tbody_th]:max-md:min-h-[var(--eg-control-touch)] [&_tbody_td]:max-md:min-h-[var(--eg-control-touch)] [&_tbody_tr]:max-md:border-t"
          data-members-matrix
        >
          <caption hlmCaption class="m-0 p-3 text-start font-semibold text-foreground max-md:block">
            {{
              copy.matrix
            }}
          </caption>
          <thead hlmTHead class="bg-secondary">
            <tr hlmTr>
              <th hlmTh scope="col">{{ copy.member }}</th>
              @for (role of matrix().columns; track role.id) {
                <th hlmTh scope="col" [id]="'matrix-role-' + role.id">{{ role.name }}</th>
              }
            </tr>
          </thead>
          <tbody hlmTBody>
            @for (row of matrix().rows; track row.membership.id) {
              <tr hlmTr>
                <th
                  hlmTh
                  scope="row"
                  class="member-identity px-3 py-2"
                  [id]="'matrix-member-' + row.membership.id"
                >
                  <span
                    class="record-label block text-xs font-medium text-muted-foreground whitespace-normal md:hidden"
                    aria-hidden="true"
                    >{{ copy.member }}</span
                  >
                  <span>{{ row.membership.invited_email }}</span>
                </th>
                @for (role of matrix().columns; track role.id) {
                  <td
                    hlmTd
                    class="role-grant px-3 py-2"
                    [attr.headers]="
                      'matrix-member-' + row.membership.id + ' matrix-role-' + role.id
                    "
                  >
                    <span
                      class="record-label block text-xs font-medium text-muted-foreground whitespace-normal md:hidden"
                      aria-hidden="true"
                      >{{ role.name }}</span
                    >
                    <span
                      class="data-[granted=true]:font-semibold"
                      [attr.data-granted]="row.granted.has(role.id) ? 'true' : null"
                    >
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
