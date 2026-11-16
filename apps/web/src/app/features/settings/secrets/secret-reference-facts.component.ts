import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { IngestTokenView } from '@evidgate/sdk';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { SECRETS_COPY, secretStatusLabel } from './secrets.copy';

@Component({
  selector: 'app-secret-reference-facts',
  imports: [AbsoluteTimeComponent, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <dl
      class="m-0 grid min-w-0 gap-[var(--eg-space-5)] sm:grid-cols-2 [&>div]:min-w-0 [&_dt]:text-muted-foreground [&_dd]:m-0 [&_dd]:wrap-anywhere"
    >
      <div>
        <dt>{{ copy.scope }}</dt>
        <dd>
          @for (scope of reference().scopes; track scope) {
            <code class="block">{{ scope }}</code>
          } @empty {
            {{ copy.unavailable }}
          }
        </dd>
      </div>
      <div>
        <dt>{{ copy.prefix }}</dt>
        <dd>
          <code>{{ reference().prefix }}</code>
        </dd>
      </div>
      <div>
        <dt>{{ copy.status }}</dt>
        <dd>
          <app-status-pill
            [label]="statusLabel(reference().status)"
            [tone]="reference().status === 'active' ? 'success' : 'neutral'"
          />
        </dd>
      </div>
      <div>
        <dt>{{ copy.expires }}</dt>
        <dd>
          @if (reference().expires_at; as at) {
            <app-absolute-time [value]="at" />
          } @else {
            {{ copy.noExpiry }}
          }
        </dd>
      </div>
      <div>
        <dt>{{ copy.overlap }}</dt>
        <dd>
          @if (reference().overlap_ends_at; as at) {
            <app-absolute-time [value]="at" />
          } @else {
            {{ copy.noOverlap }}
          }
        </dd>
      </div>
      <div>
        <dt>{{ copy.used }}</dt>
        <dd>
          @if (reference().last_used_at; as at) {
            <app-absolute-time [value]="at" />
          } @else {
            {{ copy.neverUsed }}
          }
        </dd>
      </div>
      <div>
        <dt>{{ copy.created }}</dt>
        <dd><app-absolute-time [value]="reference().created_at" /></dd>
      </div>
    </dl>
  `,
})
export class SecretReferenceFactsComponent {
  readonly reference = input.required<IngestTokenView>();
  protected readonly copy = SECRETS_COPY;
  protected readonly statusLabel = secretStatusLabel;
}
