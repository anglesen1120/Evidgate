import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { AiSettingsDocument } from '@evidgate/sdk';
import { FIELDS, textOf } from './ai-settings.model';

@Component({
  selector: 'app-ai-settings-snapshot-fields',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <dl class="hlm-field-grid @2xl/snapshot:[--hlm-field-columns:2]">
      @for (field of fields; track field.key) {
        @if (field.key !== 'reason') {
          @let value = textOf(settings(), field.key);
          <div [class.hlm-field-wide]="!inline() || needsFullRow(field.key, value)">
            <dt class="text-sm font-semibold">{{ field.label }}</dt>
            <dd class="m-0 whitespace-pre-wrap">
              <bdi>{{ value || notSet }}</bdi>
            </dd>
          </div>
        }
      }
      <div class="hlm-field-wide">
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
  readonly inline = input(false);
  protected readonly fields = FIELDS;
  protected readonly textOf = textOf;
  protected readonly notSet = $localize`:@@authoring.ai-settings.not-set:Not set`;

  protected needsFullRow(key: string, value: string): boolean {
    // Keep paragraphs and multiline guidance readable; short criteria can share a row.
    return (
      key === 'description' ||
      key === 'environment_url' ||
      value.length > 120 ||
      value.includes('\n')
    );
  }
}
