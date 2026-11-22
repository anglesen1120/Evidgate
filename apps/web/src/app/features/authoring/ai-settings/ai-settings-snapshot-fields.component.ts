import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { AiSettingsDocument } from '@evidgate/sdk';
import { FIELDS, textOf } from './ai-settings.model';

@Component({
  selector: 'app-ai-settings-snapshot-fields',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dl class="m-0 grid min-w-0 gap-(--eg-space-4) wrap-anywhere sm:grid-cols-2">
      @for (field of fields; track field.key) {
        @if (field.key !== 'reason') {
          <div class="grid min-w-0 content-start gap-(--eg-space-2)">
            <dt class="text-sm font-semibold">{{ field.label }}</dt>
            <dd class="m-0 whitespace-pre-wrap">
              <bdi>{{ textOf(settings(), field.key) || notSet }}</bdi>
            </dd>
          </div>
        }
      }
      <div class="grid min-w-0 content-start gap-(--eg-space-2)">
        <dt class="text-sm font-semibold" i18n="@@authoring.ai-settings.languages">
          Output languages
        </dt>
        <dd class="m-0">{{ settings().languages.join(', ') || notSet }}</dd>
      </div>
    </dl>
  `,
})
export class AiSettingsSnapshotFieldsComponent {
  readonly settings = input.required<AiSettingsDocument>();
  protected readonly fields = FIELDS;
  protected readonly textOf = textOf;
  protected readonly notSet = $localize`:@@authoring.ai-settings.not-set:Not set`;
}
