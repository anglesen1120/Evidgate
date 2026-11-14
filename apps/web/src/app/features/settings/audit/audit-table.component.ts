import { DatePipe } from '@angular/common';
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
  imports: [DatePipe, HlmButton, HlmTableImports, HlmTooltip, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <div
      hlmTableContainer
      class="table-scroll rounded-md border border-solid border-border bg-card md:max-h-[calc(100dvh-var(--eg-topbar-height)-var(--eg-space-8))] md:overflow-auto focus-visible:outline-[var(--eg-focus-width)] focus-visible:outline-ring"
      tabindex="0"
      role="region"
      [attr.aria-label]="copy.caption"
    >
      <table
        hlmTable
        role="table"
        class="w-full table-auto max-md:block md:min-w-[calc(var(--eg-rail-width)*3)]"
      >
        <caption hlmCaption class="m-0 p-3 text-start text-foreground max-md:block max-md:w-full">
          {{
            copy.caption
          }}
        </caption>
        <thead
          hlmTHead
          role="rowgroup"
          class="bg-secondary max-md:sr-only md:sticky md:top-0 md:z-[var(--eg-z-sticky)] [&_th]:whitespace-nowrap"
        >
          <tr hlmTr role="row">
            <th hlmTh role="columnheader" scope="col" class="w-1/6">{{ copy.time }}</th>
            <th hlmTh role="columnheader" scope="col" class="w-1/5">{{ copy.actor }}</th>
            <th hlmTh role="columnheader" scope="col" class="w-1/4">{{ copy.action }}</th>
            <th hlmTh role="columnheader" scope="col" class="w-1/5">{{ copy.object }}</th>
            <th hlmTh role="columnheader" scope="col" class="w-min">{{ copy.details }}</th>
          </tr>
        </thead>
        <tbody hlmTBody role="rowgroup" class="max-md:block">
          @for (row of rows(); track row.id) {
            <tr
              hlmTr
              role="row"
              class="max-md:grid max-md:grid-cols-2 max-md:gap-2 max-md:p-3 [&>td]:align-top [&>td]:whitespace-normal [&>td]:wrap-anywhere [&>th]:align-top [&>th]:whitespace-normal max-md:[&>td]:min-w-0 max-md:[&>td]:p-0 max-md:[&>th]:p-0"
              data-audit-row
              [attr.data-audit-support]="supportOf(row) !== null ? 'true' : 'false'"
            >
              <th
                hlmTh
                role="rowheader"
                scope="row"
                class="h-auto text-foreground max-md:order-1 max-md:col-span-2"
              >
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.time
                }}</span>
                <time
                  class="flex flex-wrap gap-x-2 md:block"
                  [attr.datetime]="row.created_at"
                  [attr.title]="row.created_at"
                >
                  <span class="block whitespace-nowrap">
                    {{ row.created_at | date: 'mediumDate' : '+0000' }}
                  </span>
                  <span class="block whitespace-nowrap">
                    {{ row.created_at | date: 'mediumTime' : '+0000' }}
                  </span>
                </time>
              </th>
              <td hlmTd role="cell" class="max-w-[var(--eg-rail-width)] max-md:order-3">
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.actor
                }}</span>
                <app-status-pill [tone]="'neutral'" [label]="actorLabel(row.actor_type)" />
                @if (row.actor_id) {
                  <span class="block wrap-anywhere">{{ row.actor_id }}</span>
                }
              </td>
              <td hlmTd role="cell" class="max-md:order-2 max-md:col-span-2">
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.action
                }}</span>
                <span class="block wrap-anywhere md:whitespace-nowrap">{{ row.action }}</span>
              </td>
              <td
                hlmTd
                role="cell"
                class="max-w-[var(--eg-rail-width)] max-md:order-4"
                [class.max-md:hidden]="!row.object_type && !row.object_id"
              >
                <span class="block text-xs text-muted-foreground md:hidden" aria-hidden="true">{{
                  copy.object
                }}</span>
                @if (row.object_type) {
                  <span>{{ row.object_type }}</span>
                }
                @if (row.object_id) {
                  <span class="block wrap-anywhere">{{ row.object_id }}</span>
                }
                @if (!row.object_type && !row.object_id) {
                  <span class="max-md:hidden">{{ copy.absent }}</span>
                }
              </td>
              <td
                hlmTd
                role="cell"
                class="max-w-[var(--eg-rail-width)] max-md:order-5 max-md:col-span-2"
                [class.max-md:hidden]="!row.request_id && !supportOf(row)"
              >
                @if (row.request_id || supportOf(row)) {
                  <details data-audit-details>
                    <summary
                      class="min-h-[var(--eg-control-touch)] cursor-pointer content-center text-sm focus-visible:outline-[var(--eg-focus-width)] focus-visible:outline-ring hover:underline"
                    >
                      {{ copy.details }}
                      @if (supportOf(row)) {
                        <app-status-pill [tone]="'warning'" [label]="copy.support" />
                      }
                    </summary>
                    <dl class="m-0 grid gap-2 text-sm">
                      @if (row.request_id) {
                        <div>
                          <dt class="text-xs text-muted-foreground">{{ copy.request }}</dt>
                          <dd class="m-0 wrap-anywhere">{{ row.request_id }}</dd>
                        </div>
                      }
                      @if (supportOf(row); as access) {
                        <div>
                          <dt class="text-xs text-muted-foreground">{{ copy.support }}</dt>
                          <dd class="m-0">
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
                            <span class="block wrap-anywhere text-xs text-muted-foreground">
                              {{ supportDescription(access.grantId) }}
                            </span>
                          </dd>
                        </div>
                      }
                    </dl>
                  </details>
                } @else {
                  <span class="max-md:hidden">{{ copy.absent }}</span>
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
    details: $localize`:@@settings.audit.details:Event details`,
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
