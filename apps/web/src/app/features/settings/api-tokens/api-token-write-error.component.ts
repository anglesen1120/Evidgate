import { JsonPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmAlertImports } from '@evidgate/ui/alert';
import type { ApiError } from '../../../core/http/api-error';
import { RetryAfterComponent } from '../../../shared/ui/conflict/retry-after.component';
import { API_TOKENS_WRITE_COPY } from './api-tokens-write.copy';

@Component({
  selector: 'app-api-token-write-error',
  imports: [HlmAlertImports, JsonPipe, RetryAfterComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error(); as failure) {
      <div
        hlmAlert
        [variant]="'destructive'"
        role="alert"
        class="grid gap-2 wrap-anywhere"
        data-api-write-error
      >
        @if (
          failure.code === 'permission_escalation' || failure.code === 'forbidden_token_permission'
        ) {
          <p class="m-0">{{ copy.escalation }}</p>
        }
        <p class="m-0">{{ failure.message }}</p>
        <code>{{ failure.code }}</code>
        @if (failure.details.length > 0) {
          <pre class="m-0 max-w-full whitespace-pre-wrap font-mono text-xs">{{
            failure.details | json
          }}</pre>
        }
        @if (failure.requestId) {
          <code>{{ failure.requestId }}</code>
        }
      </div>
      @if (failure.kind === 'rate_limited' || failure.kind === 'unavailable') {
        <app-retry-after
          [kind]="failure.kind"
          [retryAfterSeconds]="failure.retryAfterSeconds"
          [retryEligible]="false"
          [code]="failure.code"
          [requestId]="failure.requestId"
        />
      }
    }
  `,
})
export class ApiTokenWriteErrorComponent {
  readonly error = input<ApiError | null>(null);
  protected readonly copy = API_TOKENS_WRITE_COPY;
}
