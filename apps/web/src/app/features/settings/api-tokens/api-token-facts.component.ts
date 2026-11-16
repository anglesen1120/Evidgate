import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { ApiTokenView } from '@evidgate/sdk';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { API_TOKENS_COPY } from './api-tokens.copy';

export function apiTokenStateLabel(state: ApiTokenView['status']): string {
  switch (state) {
    case 'active':
      return API_TOKENS_COPY.active;
    case 'expired':
      return API_TOKENS_COPY.expired;
    case 'revoked':
      return API_TOKENS_COPY.revoked;
    default:
      return state;
  }
}

@Component({
  selector: 'app-api-token-facts',
  imports: [AbsoluteTimeComponent, StatusPillComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <dl
      class="m-0 grid min-w-0 gap-[var(--eg-space-5)] sm:grid-cols-2 [&>div]:min-w-0 [&_dt]:text-muted-foreground [&_dd]:m-0 [&_dd]:wrap-anywhere"
    >
      <div>
        <dt>{{ copy.name }}</dt>
        <dd>{{ row().name }}</dd>
      </div>
      <div>
        <dt>{{ copy.prefix }}</dt>
        <dd>
          <code>{{ row().prefix }}</code>
        </dd>
      </div>
      <div>
        <dt>{{ copy.state }}</dt>
        <dd>
          <app-status-pill
            [tone]="row().status === 'active' ? 'success' : 'neutral'"
            [label]="stateLabel(row().status)"
          />
        </dd>
      </div>
      <div>
        <dt>{{ copy.expires }}</dt>
        <dd><app-absolute-time [value]="row().expires_at" /></dd>
      </div>
      <div>
        <dt>{{ copy.lastUsed }}</dt>
        <dd>
          @if (row().last_used_at; as at) {
            <app-absolute-time [value]="at" />
          } @else {
            {{ copy.never }}
          }
        </dd>
      </div>
      <div>
        <dt>{{ createdBy }}</dt>
        <dd>
          <code>{{ row().created_by }}</code>
        </dd>
      </div>
      <div class="sm:col-span-2">
        <dt>{{ copy.permissions }}</dt>
        <dd>
          <ul class="m-0 grid gap-[var(--eg-space-1)] ps-[var(--eg-space-6)]">
            @for (permission of row().permissions; track permission) {
              <li>
                <code>{{ permission }}</code>
              </li>
            }
          </ul>
        </dd>
      </div>
    </dl>
  `,
})
export class ApiTokenFactsComponent {
  readonly row = input.required<ApiTokenView>();
  protected readonly copy = API_TOKENS_COPY;
  protected readonly stateLabel = apiTokenStateLabel;
  protected readonly createdBy = $localize`:@@settings.api-tokens.created-by:Created by`;
}
