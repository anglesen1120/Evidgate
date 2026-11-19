import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { HlmCardTitle } from '@evidgate/ui/card';
import type { AuthoringVersionHead } from '../../../core/data/authoring-editor-data.service';

@Component({
  selector: 'app-editor-version-diff',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardTitle],
  template: `
    <section class="grid min-w-0 gap-4" data-editor-diff>
      <h3 hlmCardTitle [size]="'subsection'" i18n="@@authoring.editor.diff-title">
        Stored snapshot comparison · read-only
      </h3>
      @if (left(); as before) {
        @if (right(); as after) {
          <p class="m-0" i18n="@@authoring.editor.diff-versions">
            Version {{ before.view.version_no }} → version {{ after.view.version_no }}
          </p>
          @for (row of rows(); track row.key) {
            <section class="grid min-w-0 gap-3 border-t border-border pt-4">
              <h4 class="m-0 font-semibold wrap-anywhere">{{ row.key }}</h4>
              <div class="grid min-w-0 gap-4 lg:grid-cols-2">
                <div class="min-w-0">
                  <p class="m-0 mb-2 text-sm font-semibold" [class.text-destructive]="row.changed">
                    @if (row.changed) {
                      <span i18n="@@authoring.editor.diff-before">− Before</span>
                    } @else {
                      <span i18n="@@authoring.editor.diff-unchanged">Unchanged</span>
                    }
                  </p>
                  <pre class="m-0 whitespace-pre-wrap text-sm wrap-anywhere">{{ row.before }}</pre>
                </div>
                <div class="min-w-0">
                  <p class="m-0 mb-2 text-sm font-semibold">
                    @if (row.changed) {
                      <span i18n="@@authoring.editor.diff-after">+ After</span>
                    } @else {
                      <span i18n="@@authoring.editor.diff-unchanged">Unchanged</span>
                    }
                  </p>
                  <pre class="m-0 whitespace-pre-wrap text-sm wrap-anywhere">{{ row.after }}</pre>
                </div>
              </div>
            </section>
          } @empty {
            <p i18n="@@authoring.editor.diff-empty">Both stored snapshots are empty.</p>
          }
        }
      }
    </section>
  `,
})
export class VersionDiffComponent {
  readonly left = input<AuthoringVersionHead>();
  readonly right = input<AuthoringVersionHead>();
  private readonly missing = $localize`:@@authoring.editor.snapshot-missing:Not present in this snapshot`;
  protected readonly rows = computed(() => {
    const before = this.left()?.view;
    const after = this.right()?.view;
    if (
      !before ||
      !after ||
      before.entity_kind !== after.entity_kind ||
      before.entity_id !== after.entity_id
    )
      return [];
    const keys = [
      ...new Set([...Object.keys(before.content), ...Object.keys(after.content)]),
    ].sort();
    return keys.map((key) => {
      const left = Object.hasOwn(before.content, key)
        ? JSON.stringify(before.content[key], null, 2)
        : this.missing;
      const right = Object.hasOwn(after.content, key)
        ? JSON.stringify(after.content[key], null, 2)
        : this.missing;
      return { key, before: left, after: right, changed: left !== right };
    });
  });
}
