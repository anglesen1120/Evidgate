import { ChangeDetectionStrategy, Component, ElementRef, inject, input } from '@angular/core';
import type { AuditEntryDto } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmTableImports } from '@evidgate/ui/table';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { AUDIT_ACTOR_TYPE_LABELS } from './actor-type-labels';
import { supportAccessOf } from './support-access';

@Component({
  selector: 'app-audit-table',
  imports: [HlmButton, HlmTableImports, HlmTooltip, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <div
      hlmTableContainer
      class="table-scroll rounded-md border border-solid border-border bg-card focus-visible:outline-[var(--eg-focus-width)] focus-visible:outline-ring"
      tabindex="0"
      role="region"
      [attr.aria-label]="copy.caption"
    >
      <table hlmTable role="table" class="w-full table-fixed max-md:block">
        <caption hlmCaption class="m-0 p-3 text-start text-foreground max-md:block max-md:w-full">
          {{
            copy.caption
          }}
        </caption>
        <thead hlmTHead role="rowgroup" class="bg-secondary max-md:sr-only">
          <tr hlmTr role="row">
            <th hlmTh role="columnheader" scope="col">{{ copy.time }}</th>
            <th hlmTh role="columnheader" scope="col">{{ copy.actor }}</th>
            <th hlmTh role="columnheader" scope="col">{{ copy.action }}</th>
            <th hlmTh role="columnheader" scope="col">{{ copy.object }}</th>
            <th hlmTh role="columnheader" scope="col">{{ copy.request }}</th>
            <th hlmTh role="columnheader" scope="col">{{ copy.support }}</th>
          </tr>
        </thead>
        <tbody hlmTBody role="rowgroup" class="max-md:block">
          @for (row of rows(); track row.id) {
            <tr
              hlmTr
              role="row"
              class="max-md:grid max-md:gap-2 max-md:p-3 [&>td]:align-top [&>td]:whitespace-normal [&>td]:wrap-anywhere [&>th]:align-top [&>th]:whitespace-normal [&>th]:wrap-anywhere max-md:[&>td]:min-h-[var(--eg-control-touch)] max-md:[&>td]:p-0 max-md:[&>th]:p-0"
              data-audit-row
              [attr.data-audit-support]="supportOf(row) !== null ? 'true' : 'false'"
            >
              <th hlmTh role="rowheader" scope="row" class="h-auto text-foreground">
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.time
                }}</span>
                <time [attr.datetime]="row.created_at">{{ row.created_at }}</time>
              </th>
              <td hlmTd role="cell">
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.actor
                }}</span>
                <app-status-pill [tone]="'neutral'" [label]="actorLabel(row.actor_type)" />
                <span class="block wrap-anywhere">{{ row.actor_id }}</span>
              </td>
              <td hlmTd role="cell">
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.action
                }}</span>
                {{ row.action }}
              </td>
              <td hlmTd role="cell">
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.object
                }}</span>
                <span>{{ row.object_type ?? copy.absent }}</span>
                <span class="block wrap-anywhere">{{ row.object_id ?? copy.absent }}</span>
              </td>
              <td hlmTd role="cell">
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.request
                }}</span>
                {{ row.request_id ?? copy.absent }}
              </td>
              <td hlmTd role="cell">
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.support
                }}</span>
                @if (supportOf(row); as access) {
                  <button
                    hlmBtn
                    [variant]="'ghost'"
                    type="button"
                    class="support-access h-auto min-h-[var(--eg-control-touch)] max-w-full whitespace-normal p-0"
                    [hlmTooltip]="supportDescription(access.grantId)"
                    [position]="'top'"
                  >
                    <app-status-pill [tone]="'warning'" [label]="copy.support" />
                  </button>
                  <span class="block wrap-anywhere text-xs text-muted-foreground md:hidden">
                    {{ supportDescription(access.grantId) }}
                  </span>
                } @else {
                  {{ copy.absent }}
                }
              </td>
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class AuditTableComponent {
  readonly rows = input.required<readonly AuditEntryDto[]>();
  private readonly element = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly supportOf = supportAccessOf;
  protected readonly copy = {
    caption: $localize`:@@settings.audit.caption:Audit activity`,
    time: $localize`:@@settings.audit.time:Time (UTC)`,
    actor: $localize`:@@settings.audit.actor:Actor`,
    action: $localize`:@@settings.audit.action:Action`,
    object: $localize`:@@settings.audit.object:Object`,
    request: $localize`:@@settings.audit.request:Request id`,
    support: $localize`:@@settings.audit.support:Support access`,
    absent: $localize`:@@settings.audit.absent:—`,
  };

  protected actorLabel(actorType: string): string {
    return Object.hasOwn(AUDIT_ACTOR_TYPE_LABELS, actorType)
      ? AUDIT_ACTOR_TYPE_LABELS[actorType as keyof typeof AUDIT_ACTOR_TYPE_LABELS]
      : actorType;
  }

  protected supportDescription(grantId: string | null): string {
    return grantId
      ? $localize`:@@settings.audit.support-description:Platform staff access under support grant ${grantId}:grantId:`
      : $localize`:@@settings.audit.support-unrecorded:Platform staff access (grant id not recorded on this row)`;
  }

  resetScroll(): void {
    const region = this.element.nativeElement.querySelector<HTMLElement>('.table-scroll');
    if (region) {
      region.scrollLeft = 0;
      region.scrollTop = 0;
    }
  }
}
