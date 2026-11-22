import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { ManualCaseDetailDto } from '@evidgate/sdk';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';

@Component({
  selector: 'app-editor-record-summary',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmCard,
    HlmCardTitle,
    ShellIconComponent,
    StatusPillComponent,
    CopyableIdentifierComponent,
    AbsoluteTimeComponent,
  ],
  host: { class: 'block min-w-0 @container' },
  template: `
    @if (panel() === 'record') {
      <aside hlmCard class="min-w-0 gap-0 py-0" aria-labelledby="case-record-heading">
        <h2
          hlmCardTitle
          id="case-record-heading"
          [size]="'subsection'"
          class="border-b border-border px-(--eg-space-5) py-(--eg-space-4)"
          i18n="@@authoring.editor.authoring-record"
        >
          Authoring record
        </h2>
        <dl
          class="m-0 grid min-w-0 gap-(--eg-space-4) p-(--eg-space-5) [&>div]:min-w-0 [&>div]:rounded-(--eg-radius-md) [&>div]:bg-muted [&>div]:p-(--eg-space-4) [&_dt]:text-sm [&_dt]:font-semibold [&_dd]:m-0 [&_dd]:mt-(--eg-space-2)"
        >
          <div>
            <dt>{{ caseIdLabel }}</dt>
            <dd>
              <app-copyable-identifier
                [value]="view().case_id"
                [label]="caseIdLabel"
                [compact]="true"
              />
            </dd>
          </div>
          <div>
            <dt i18n="@@authoring.editor.last-updated">Last updated</dt>
            <dd><app-absolute-time [value]="view().updated_at" /></dd>
          </div>
          <div>
            <dt i18n="@@authoring.editor.provenance">Provenance</dt>
            <dd
              class="text-sm text-muted-foreground"
              i18n="@@authoring.editor.live-provenance-unavailable"
            >
              Source and author are unavailable in the case detail. Timeline shows the metadata
              supplied for each version.
            </dd>
          </div>
        </dl>
      </aside>
    } @else {
      <dl
        hlmCard
        class="m-0 grid min-w-0 gap-px overflow-hidden bg-border py-0 @min-[30rem]:grid-cols-2 @min-[45rem]:grid-cols-4 [&>div]:grid [&>div]:min-w-0 [&>div]:content-start [&>div]:gap-(--eg-space-2) [&>div]:bg-card [&>div]:p-(--eg-space-4) [&_dt]:flex [&_dt]:items-center [&_dt]:gap-(--eg-space-3) [&_dt]:text-sm [&_dt]:font-semibold [&_dd]:m-0 [&_dd]:min-w-0 [&_app-shell-icon]:box-content [&_app-shell-icon]:rounded-(--eg-radius-md) [&_app-shell-icon]:p-(--eg-space-2)"
      >
        <div>
          <dt>
            <app-shell-icon [name]="'book'" class="bg-(--eg-info-bg) text-(--eg-info-fg)" />
            <span i18n="@@authoring.editor.manual-case">Manual case</span>
          </dt>
          <dd>
            <app-copyable-identifier [value]="view().code" [label]="codeLabel" [compact]="true" />
          </dd>
        </div>
        <div>
          <dt>
            <app-shell-icon [name]="'layers'" class="bg-(--eg-accent-soft) text-primary" />
            <span i18n="@@authoring.editor.revision">Revision</span>
          </dt>
          <dd
            class="eg-type-subsection-title tabular-nums"
            i18n="@@authoring.editor.current-version"
          >
            Current version: {{ view().version }}
          </dd>
          <dd class="eg-type-caption text-muted-foreground" i18n="@@authoring.editor.approved-use">
            Only approved revisions are used for runs and generated scripts.
          </dd>
        </div>
        <div>
          <dt>
            <app-shell-icon [name]="'list'" class="bg-(--eg-neutral-bg) text-(--eg-neutral-fg)" />
            <span i18n="@@authoring.editor.authoring-status">Authoring status</span>
          </dt>
          <dd>
            <app-status-pill
              [label]="lifecycle[view().state]"
              [dimension]="lifecycleLabel"
              [tone]="view().state === 'approved' ? 'success' : 'neutral'"
            />
          </dd>
          <dd class="eg-type-caption text-muted-foreground" i18n="@@authoring.editor.not-outcome">
            Not an execution result
          </dd>
        </div>
        <div>
          <dt>
            <app-shell-icon [name]="'tree'" class="bg-(--eg-accent-soft) text-primary" />
            {{ parentLabel }}
          </dt>
          <dd>
            <app-copyable-identifier
              [value]="view().checklist_item_id"
              [label]="parentLabel"
              [compact]="true"
            />
          </dd>
        </div>
      </dl>
    }
  `,
})
export class EditorRecordSummaryComponent {
  readonly view = input.required<ManualCaseDetailDto>();
  readonly panel = input<'summary' | 'record'>('summary');
  protected readonly caseIdLabel = $localize`:@@authoring.editor.case-id:Case ID`;
  protected readonly codeLabel = $localize`:@@authoring.editor.case-code:Case code`;
  protected readonly parentLabel = $localize`:@@authoring.editor.checklist-parent:Checklist parent`;
  protected readonly lifecycleLabel = $localize`:@@authoring.editor.lifecycle:Status`;
  protected readonly lifecycle = {
    draft: $localize`:@@authoring.editor.lifecycle-draft:Draft`,
    approved: $localize`:@@authoring.editor.lifecycle-approved:Approved`,
    retired: $localize`:@@authoring.editor.lifecycle-retired:Retired`,
  };
}
