import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { CASE_FIELDS, REASON_UNAVAILABLE, STEP_FIELDS, type CaseDraft } from './editor.model';

@Component({
  selector: 'app-editor-readonly-values',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'grid min-w-0 gap-(--eg-space-4)' },
  template: `
    @if (dirty()) {
      <p class="m-0 text-muted-foreground" i18n="@@authoring.editor.narrow-draft">
        Unsaved desktop edits are retained, not saved. Open a wider screen to edit.
      </p>
    } @else {
      <p class="m-0 text-muted-foreground" i18n="@@authoring.editor.narrow-saved">
        These saved values are shown read-only on a narrow screen. Open a wider screen to edit them.
      </p>
    }
    <dl class="m-0 grid min-w-0 gap-(--eg-space-4) wrap-anywhere">
      @for (field of fields; track field.key) {
        <div>
          <dt class="font-semibold">{{ field.label }}</dt>
          <dd class="m-0 whitespace-pre-wrap">{{ draft()[field.key] || notSet }}</dd>
        </div>
      }
      <div>
        <dt class="font-semibold" i18n="@@authoring.editor.priority">Priority</dt>
        <dd class="m-0">{{ priorityLabel() }}</dd>
      </div>
    </dl>
    <ol
      class="m-0 grid min-w-0 list-decimal gap-(--eg-space-4) ps-(--eg-space-6) marker:font-semibold"
    >
      @for (step of draft().steps; track step.id) {
        <li>
          <dl
            class="m-0 grid min-w-0 gap-(--eg-space-3) rounded-(--eg-radius-md) bg-muted p-(--eg-space-4) wrap-anywhere"
          >
            @for (field of stepFields; track field.key) {
              <div>
                <dt class="font-semibold">{{ field.label }}</dt>
                <dd class="m-0 whitespace-pre-wrap">{{ step[field.key] || notSet }}</dd>
              </div>
            }
          </dl>
        </li>
      }
    </ol>
    <p class="m-0 text-sm text-muted-foreground">{{ reasonUnavailable }}</p>
  `,
})
export class EditorReadonlyValuesComponent {
  readonly draft = input.required<CaseDraft>();
  readonly dirty = input.required<boolean>();
  readonly priorityLabel = input.required<string>();
  protected readonly fields = CASE_FIELDS;
  protected readonly stepFields = STEP_FIELDS;
  protected readonly reasonUnavailable = REASON_UNAVAILABLE;
  protected readonly notSet = $localize`:@@authoring.editor.not-set:Not set`;
}
