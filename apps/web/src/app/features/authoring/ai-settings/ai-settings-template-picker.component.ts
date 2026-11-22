import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import type { AiSettingsTemplateView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardContent, HlmCardHeader, HlmCardTitle } from '@evidgate/ui/card';
import type { ApiError } from '../../../core/http/api-error';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';

@Component({
  selector: 'app-ai-settings-template-picker',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmCardContent, HlmCardHeader, HlmCardTitle, RequestStateComponent],
  template: `
    <header hlmCardHeader class="border-b border-border">
      <h2 hlmCardTitle [size]="'subsection'" i18n="@@authoring.ai-settings.templates">
        Built-in templates
      </h2>
    </header>
    <div hlmCardContent>
      <app-request-state
        [status]="status()"
        [error]="error()"
        [retryEligible]="true"
        (retry)="retry.emit()"
      >
        <div class="grid min-w-0 gap-(--eg-space-3)">
          @for (template of templates(); track template.code + ':' + template.version) {
            <button
              hlmBtn
              type="button"
              [variant]="'outline'"
              data-ai-template
              #opener
              class="min-h-(--eg-control-touch) w-full whitespace-normal wrap-anywhere"
              [disabled]="disabled()"
              (click)="choose.emit({ template, opener })"
            >
              {{ template.name }} ({{ template.locale }})
            </button>
          } @empty {
            <p
              class="m-0 text-sm text-muted-foreground"
              i18n="@@authoring.ai-settings.no-templates"
            >
              No templates are available.
            </p>
          }
        </div>
      </app-request-state>
    </div>
  `,
})
export class AiSettingsTemplatePickerComponent {
  readonly templates = input.required<readonly AiSettingsTemplateView[]>();
  readonly status = input.required<'loading' | 'error' | 'ready'>();
  readonly error = input.required<ApiError | null>();
  readonly disabled = input.required<boolean>();
  readonly retry = output<void>();
  readonly choose = output<{ template: AiSettingsTemplateView; opener: HTMLButtonElement }>();
}
