import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow/desktop-required-notice.component';
import {
  EmptyPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
  NotFoundPatternComponent,
} from '../../../shared/ui/states';
import {
  draftEditorFixture,
  editorScene,
  type EditorScene,
  type StudioDraftEditorViewModel,
} from './draft-editor.fixture';
import { editorCopy as c } from './editor-copy';
import { EditorDefinitionComponent } from './editor-definition.component';
import { EditorSandboxComponent } from './editor-sandbox.component';
import { EditorValidationComponent } from './editor-validation.component';
import { EditorVersionsComponent } from './editor-versions.component';

const notices: Partial<Record<EditorScene, string>> = {
  '403': c.forbidden,
  '412': c.conflict,
  '429': c.throttled,
  readonly: c.readOnly,
  published: c.published,
  merged: c.merged,
  running: c.running,
  failed: c.failed,
  passed: c.passed,
};

// Review only until draft editing is implemented. The behavior task replaces the fixture with the data service.
@Component({
  selector: 'app-studio-draft-editor-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    PageHeaderImports,
    ShellIconComponent,
    HlmButton,
    HlmCardImports,
    StatusPillComponent,
    DesktopRequiredNoticeComponent,
    EmptyPatternComponent,
    LoadingPatternComponent,
    InlineFeedbackComponent,
    NotFoundPatternComponent,
    EditorDefinitionComponent,
    EditorValidationComponent,
    EditorSandboxComponent,
    EditorVersionsComponent,
  ],
  host: {
    class:
      'mx-auto grid w-full min-w-0 max-w-[1440px] gap-(--eg-space-5) text-foreground wrap-anywhere',
  },
  templateUrl: './studio-draft-editor-review.page.html',
})
export class StudioDraftEditorReviewPage {
  private readonly route = inject(ActivatedRoute);
  private readonly router = inject(Router);
  private readonly params = toSignal(this.route.queryParamMap);
  readonly model = input<StudioDraftEditorViewModel>(draftEditorFixture);
  protected readonly c = c;
  protected readonly state = computed(() => editorScene(this.params()?.get('state') ?? null));
  protected readonly hasDraft = computed(
    () => !['loading', 'empty', 'error', '401', '404'].includes(this.state()),
  );
  protected readonly notice = computed(() => notices[this.state()]);
  // Switching specimens starts a fresh local buffer; edits never travel to another revision.
  protected readonly editorScope = computed(() => [this.state()]);
  protected retry(): void {
    void this.router.navigate([], {
      relativeTo: this.route,
      queryParams: { state: null },
      queryParamsHandling: 'merge',
    });
  }
}
