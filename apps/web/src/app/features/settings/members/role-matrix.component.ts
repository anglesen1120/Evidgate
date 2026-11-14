import { ChangeDetectionStrategy, Component, computed, input, signal } from '@angular/core';
import type { MembershipView, RoleView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmTableImports } from '@evidgate/ui/table';
import { HlmCardTitle } from '@evidgate/ui/card';
import { DataTableValueComponent } from '../../../shared/ui/data-table/data-table-value.component';
import { MEMBERS_COPY } from './member-list.component';

@Component({
  selector: 'app-role-matrix',
  imports: [HlmButton, HlmTableImports, HlmCardTitle, DataTableValueComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    @if (matrix().columns.length) {
      <section class="mb-3 grid min-w-0 gap-3 md:hidden" [attr.aria-label]="copy.matrix">
        <h3 hlmCardTitle [size]="subsectionHeadingSize" class="m-0">{{ copy.matrix }}</h3>
        @if (!fullMatrix()) {
          <dl class="m-0 grid min-w-0 gap-3" data-members-granted-summary>
            @for (row of grantedRows(); track row.membership.id) {
              <div class="grid min-w-0 gap-1">
                <dt class="font-semibold wrap-anywhere">{{ row.membership.invited_email }}</dt>
                <dd class="m-0 flex flex-wrap gap-2 wrap-anywhere">
                  @for (role of row.roles; track role.id) {
                    <span>{{ role.name }}</span>
                  } @empty {
                    {{ copy.noRoles }}
                  }
                </dd>
              </div>
            }
          </dl>
        }
        <button
          hlmBtn
          [variant]="'outline'"
          type="button"
          class="min-h-[var(--eg-control-touch)] w-fit max-w-full whitespace-normal"
          [attr.aria-expanded]="fullMatrix()"
          [attr.aria-controls]="'members-full-matrix'"
          (click)="fullMatrix.set(!fullMatrix())"
        >
          {{ fullMatrix() ? grantedOnly : showFullMatrix }}
        </button>
      </section>
      <div
        hlmTableContainer
        class="table-scroll min-w-0 max-w-full rounded-md border border-solid border-border bg-card max-md:overflow-visible focus-visible:outline-[var(--eg-focus-width)] focus-visible:outline-ring focus-visible:outline-offset-[var(--eg-focus-offset)]"
        id="members-full-matrix"
        [class.max-md:hidden]="!fullMatrix()"
        tabindex="0"
        role="region"
        [attr.aria-label]="copy.matrix"
      >
        <table
          hlmTable
          role="table"
          class="eg-table-layout caption-top border-collapse max-md:block max-md:min-w-0 max-md:max-w-full [&_tbody]:max-md:block [&_tr]:max-md:block [&_thead]:max-md:sr-only [&_th]:break-normal [&_td]:break-normal [&_tbody_th]:max-md:grid [&_tbody_td]:max-md:grid [&_tbody_th]:max-md:gap-1 [&_tbody_td]:max-md:gap-1 [&_tbody_th]:max-md:whitespace-normal [&_tbody_td]:max-md:whitespace-normal [&_tbody_th]:max-md:wrap-anywhere [&_tbody_td]:max-md:wrap-anywhere [&_tbody_th]:max-md:min-h-[var(--eg-control-touch)] [&_tbody_td]:max-md:min-h-[var(--eg-control-touch)] [&_tbody_tr]:max-md:border-t"
          data-members-matrix
        >
          <caption hlmCaption class="m-0 p-3 text-start font-semibold text-foreground max-md:block">
            {{
              copy.matrix
            }}
          </caption>
          <thead hlmTHead role="rowgroup" class="bg-secondary">
            <tr hlmTr role="row">
              <th
                hlmTh
                [hlmTableColumn]="identityRole"
                [columnKind]="emailKind"
                [columnPriority]="primaryPriority"
                role="columnheader"
                scope="col"
              >
                {{ copy.member }}
              </th>
              @for (role of matrix().columns; track role.id) {
                <th
                  hlmTh
                  [hlmTableColumn]="statusRole"
                  role="columnheader"
                  scope="col"
                  [id]="'matrix-role-' + role.id"
                  [textContent]="role.name"
                ></th>
              }
            </tr>
          </thead>
          <tbody hlmTBody role="rowgroup">
            @for (row of matrix().rows; track row.membership.id) {
              <tr hlmTr role="row">
                <th
                  hlmTh
                  [hlmTableColumn]="identityRole"
                  [columnKind]="emailKind"
                  [columnPriority]="primaryPriority"
                  role="rowheader"
                  scope="row"
                  class="member-identity px-3 py-2 max-md:w-full max-md:min-w-0 max-md:max-w-full"
                  [id]="'matrix-member-' + row.membership.id"
                >
                  <span
                    class="record-label block text-xs font-medium text-muted-foreground whitespace-normal md:hidden"
                    >{{ copy.member }}</span
                  >
                  <app-data-table-value [value]="row.membership.invited_email" [truncate]="true" />
                </th>
                @for (role of matrix().columns; track role.id) {
                  <td
                    hlmTd
                    [hlmTableColumn]="statusRole"
                    role="cell"
                    class="role-grant px-3 py-2 max-md:w-full max-md:min-w-0 max-md:max-w-full"
                    [attr.headers]="
                      'matrix-member-' + row.membership.id + ' matrix-role-' + role.id
                    "
                  >
                    <span
                      class="record-label block text-xs font-medium text-muted-foreground whitespace-normal md:hidden"
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
  protected readonly subsectionHeadingSize = 'subsection' as const;
  protected readonly identityRole = 'identity' as const;
  protected readonly statusRole = 'status' as const;
  protected readonly emailKind = 'email' as const;
  protected readonly primaryPriority = 'primary' as const;
  protected readonly fullMatrix = signal(false);
  protected readonly grantedRows = computed(() =>
    this.matrix().rows.map((row) => ({
      membership: row.membership,
      roles: this.matrix().columns.filter((role) => row.granted.has(role.id)),
    })),
  );
  protected readonly showFullMatrix = $localize`:@@settings.members.show-full-matrix:Show all roles`;
  protected readonly grantedOnly = $localize`:@@settings.members.show-granted-only:Show granted roles only`;
}
