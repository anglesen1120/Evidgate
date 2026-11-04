import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { RotationReminderChipComponent } from './rotation-reminder-chip.component';

export const SECRETS_COPY = {
  title: $localize`:@@settings.secrets.title:Secret references`,
  description: $localize`:@@settings.secrets.description:Inspect reference metadata and rotation timing. Current credential values cannot be fetched or displayed.`,
  project: $localize`:@@settings.secrets.project:Project`,
  scope: $localize`:@@settings.secrets.scope:Resolution scope`,
  prefix: $localize`:@@settings.secrets.prefix:Token prefix`,
  status: $localize`:@@settings.secrets.status:Rotation state`,
  expires: $localize`:@@settings.secrets.expires:Expires · UTC`,
  overlap: $localize`:@@settings.secrets.overlap:Overlap ends · UTC`,
  used: $localize`:@@settings.secrets.used:Last used · UTC`,
  created: $localize`:@@settings.secrets.created:Created · UTC`,
  unavailable: $localize`:@@settings.secrets.unavailable:Unavailable`,
  noExpiry: $localize`:@@settings.secrets.no-expiry:No expiry`,
  noOverlap: $localize`:@@settings.secrets.no-overlap:No overlap window`,
  neverUsed: $localize`:@@settings.secrets.never-used:Not used yet`,
  rotate: $localize`:@@settings.secrets.rotate:Rotate credential`,
  submitting: $localize`:@@settings.secrets.submitting:Rotating…`,
  emptyTitle: $localize`:@@settings.secrets.empty-title:No secret references`,
  emptyDescription: $localize`:@@settings.secrets.empty-description:Ingest token references will appear here for the selected project.`,
  noProjects: $localize`:@@settings.secrets.no-projects:No projects available in this organization.`,
  more: $localize`:@@settings.secrets.more:Load more references`,
  previous: $localize`:@@settings.secrets.previous:Previous references`,
  next: $localize`:@@settings.secrets.next:Next references`,
  pagingFailure: $localize`:@@settings.secrets.paging-failure:The next page could not be loaded. References already shown are unchanged.`,
  saved: $localize`:@@settings.secrets.saved:Rotation recorded. Update your downstream CI with the new credential and verify it there.`,
  narrow: $localize`:@@settings.secrets.narrow:Use a wider screen to rotate credentials.`,
  pagesLabel: $localize`:@@settings.secrets.pages-label:Secret references pages`,
  rotateUnavailableHint: $localize`:@@settings.secrets.rotate-unavailable-hint:The rotation may have been applied. Reload the list and check the status before rotating again.`,
  reload: $localize`:@@settings.secrets.reload:Reload`,
};

@Component({
  selector: 'app-secret-reference-list',
  imports: [StatusPillComponent, RotationReminderChipComponent, HlmButton, HlmCard],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: {
    class:
      'block min-w-0 [&_h3]:m-0 [&_dd]:m-0 [&_dt]:text-xs [&_dt]:text-muted-foreground [&_code]:font-mono',
  },
  // Inline keeps the explicit metadata bindings visible to the secret-rendering lint rule.
  template: `
    <ul class="m-0 grid list-none gap-3 p-0" [attr.aria-label]="copy.title">
      @for (row of rows(); track row.id) {
        <li hlmCard class="min-w-0 gap-3 p-3 wrap-anywhere md:p-4" data-secret-reference>
          <div class="flex flex-wrap items-start gap-2 md:items-center">
            <h3 class="min-w-0 basis-full text-base md:basis-auto md:grow">{{ row.name }}</h3>
            <app-status-pill
              [tone]="
                row.status === 'active'
                  ? 'success'
                  : row.status === 'expired'
                    ? 'danger'
                    : 'neutral'
              "
              [dimension]="copy.status"
              [label]="statusLabel(row.status)"
            />
            <app-rotation-reminder-chip [reference]="row" />
          </div>
          <dl class="m-0 grid min-w-0 gap-3 md:grid-cols-2 [&>div]:min-w-0">
            <div>
              <dt>{{ copy.scope }}</dt>
              <dd>
                @for (scope of row.scopes; track scope) {
                  <code class="block">{{ scope }}</code>
                } @empty {
                  {{ copy.unavailable }}
                }
              </dd>
            </div>
            <div>
              <dt>{{ copy.prefix }}</dt>
              <dd>
                <code>{{ row.prefix }}</code>
              </dd>
            </div>
            <div>
              <dt>{{ copy.expires }}</dt>
              <dd>
                @if (row.expires_at; as date) {
                  <time [attr.datetime]="date">{{ date }}</time>
                } @else {
                  {{ copy.noExpiry }}
                }
              </dd>
            </div>
            <div>
              <dt>{{ copy.overlap }}</dt>
              <dd>
                @if (row.overlap_ends_at; as date) {
                  <time [attr.datetime]="date">{{ date }}</time>
                } @else {
                  {{ copy.noOverlap }}
                }
              </dd>
            </div>
            <div>
              <dt>{{ copy.used }}</dt>
              <dd>
                @if (row.last_used_at; as date) {
                  <time [attr.datetime]="date">{{ date }}</time>
                } @else {
                  {{ copy.neverUsed }}
                }
              </dd>
            </div>
            <div>
              <dt>{{ copy.created }}</dt>
              <dd>
                <time [attr.datetime]="row.created_at">{{ row.created_at }}</time>
              </dd>
            </div>
          </dl>
          @if (canWrite() && row.status === 'active') {
            <button
              hlmBtn
              [variant]="'outline'"
              type="button"
              class="hidden justify-self-start md:inline-flex"
              data-rotate
              [disabled]="busy() || blocked()"
              [attr.data-reference-id]="row.id"
              [attr.aria-label]="rotateLabel(row.name)"
              [attr.aria-busy]="busy() && busyId() === row.id"
              (click)="requestRotate(row, $event)"
            >
              {{ busy() && busyId() === row.id ? copy.submitting : copy.rotate }}
            </button>
          }
        </li>
      }
    </ul>
  `,
})
export class SecretReferenceListComponent {
  readonly rows = input.required<readonly IngestTokenView[]>();
  readonly canWrite = input(false);
  readonly busy = input(false);
  readonly blocked = input(false);
  readonly busyId = input<string | null>(null);
  readonly rotate = output<{ reference: IngestTokenView; opener: HTMLElement }>();
  protected readonly copy = SECRETS_COPY;
  protected statusLabel(status: IngestTokenView['status']): string {
    switch (status) {
      case 'active':
        return $localize`:@@settings.secrets.status-active:Active`;
      case 'rotated':
        return $localize`:@@settings.secrets.status-rotated:Rotated`;
      case 'revoked':
        return $localize`:@@settings.secrets.status-revoked:Revoked`;
      case 'expired':
        return $localize`:@@settings.secrets.status-expired:Expired`;
      default:
        return status;
    }
  }
  protected rotateLabel(name: string): string {
    return $localize`:@@settings.secrets.rotate-named:Rotate credential ${name}:name:`;
  }
  protected requestRotate(reference: IngestTokenView, event: Event): void {
    if (
      this.canWrite() &&
      !this.busy() &&
      !this.blocked() &&
      reference.status === 'active' &&
      event.currentTarget instanceof HTMLElement
    )
      this.rotate.emit({ reference, opener: event.currentTarget });
  }
}
