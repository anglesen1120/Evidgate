import { ChangeDetectionStrategy, Component, ElementRef, inject, input } from '@angular/core';
import type { AuditEntryDto } from '@evidgate/sdk';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { AUDIT_ACTOR_TYPE_LABELS } from './actor-type-labels';
import { supportAccessOf } from './support-access';

@Component({
  selector: 'app-audit-table',
  standalone: true,
  imports: [StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './audit.page.css',
  template: `
    <div class="table-scroll" tabindex="0" role="region" [attr.aria-label]="copy.caption">
      <table>
        <caption>
          {{
            copy.caption
          }}
        </caption>
        <thead>
          <tr>
            <th scope="col">{{ copy.time }}</th>
            <th scope="col">{{ copy.actor }}</th>
            <th scope="col">{{ copy.action }}</th>
            <th scope="col">{{ copy.object }}</th>
            <th scope="col">{{ copy.request }}</th>
            <th scope="col">{{ copy.support }}</th>
          </tr>
        </thead>
        <tbody>
          @for (row of rows(); track row.id) {
            <tr
              data-audit-row
              [attr.data-audit-support]="supportOf(row) !== null ? 'true' : 'false'"
            >
              <th scope="row">
                <time [attr.datetime]="row.created_at">{{ row.created_at }}</time>
              </th>
              <td>
                <app-status-pill [tone]="'neutral'" [label]="actorLabel(row.actor_type)" /><span
                  class="identifier"
                  >{{ row.actor_id }}</span
                >
              </td>
              <td>{{ row.action }}</td>
              <td>
                <span>{{ row.object_type ?? copy.absent }}</span
                ><span class="identifier">{{ row.object_id ?? copy.absent }}</span>
              </td>
              <td class="identifier">{{ row.request_id ?? copy.absent }}</td>
              <td>
                @if (supportOf(row); as access) {
                  <span
                    class="support-access"
                    role="group"
                    tabindex="0"
                    [attr.aria-label]="copy.support"
                    [attr.aria-describedby]="'audit-support-' + row.id"
                    (mouseenter)="description.hidden = false"
                    (focusin)="description.hidden = false"
                    (keydown.escape)="description.hidden = true; $event.stopPropagation()"
                  >
                    <app-status-pill [tone]="'warning'" [label]="copy.support" />
                    <span
                      #description
                      class="support-description"
                      role="tooltip"
                      [id]="'audit-support-' + row.id"
                      >{{ supportDescription(access.grantId) }}</span
                    >
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
