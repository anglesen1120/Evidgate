import { DOCUMENT } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardTitle } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTabsImports } from '@evidgate/ui/tabs';
import { createFormFeedback } from '../../../shared/forms/form-feedback';
import { FieldFeedbackComponent } from '../../../shared/forms/field-feedback.component';
import { FormErrorSummaryComponent } from '../../../shared/forms/form-error-summary.component';
import {
  FieldRequirementComponent,
  FormRequiredNoteComponent,
} from '../../../shared/forms/form-requirement.component';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { ConflictDialogComponent } from '../../../shared/ui/conflict/conflict-dialog.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { DesktopRequiredNoticeComponent, NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { StatusPillComponent } from '../../../shared/ui/chips/status-pill.component';
import { EditorStore } from './editor.store';
import { TimelineComponent } from './timeline.component';
import {
  CASE_FIELDS,
  STEP_FIELDS,
  PRIORITIES,
  REASON_UNAVAILABLE,
  WRITE_REASON,
  SERVER_FIELD_ERROR,
  codePoints,
  errorOf,
  textError,
  type CaseTextField,
  type StepTextField,
} from './editor.model';

/** Tab labels the narrow panel select shows; the tab list carries the same translations. */
const PANEL_LABELS: Readonly<Record<string, string>> = {
  editor: $localize`:@@authoring.editor.editor-tab:Editor`,
  timeline: $localize`:@@authoring.editor.timeline-tab:Timeline`,
};

@Component({
  selector: 'app-authoring-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    HlmButton,
    HlmCardTitle,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    HlmTabsImports,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    FormRequiredNoteComponent,
    FieldRequirementComponent,
    AbsoluteTimeComponent,
    ConflictDialogComponent,
    PageHeaderComponent,
    DesktopRequiredNoticeComponent,
    RequestStateComponent,
    CopyableIdentifierComponent,
    StatusPillComponent,
    TimelineComponent,
  ],
  providers: [EditorStore],
  templateUrl: './editor.page.html',
  host: {
    class: 'block min-w-0 text-foreground',
    '(focusin)': 'rememberFocus($event)',
    '(window:beforeunload)': 'beforeUnload($event)',
  },
})
export class EditorPage {
  protected readonly store = inject(EditorStore);
  protected readonly viewport = inject(NarrowMutationPolicy);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);
  private readonly destroy = inject(DestroyRef);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly pageHeadingLevel =
    typeof this.route.snapshot.data['pageHeadingLevel'] === 'number'
      ? this.route.snapshot.data['pageHeadingLevel']
      : 1;
  protected readonly fields = CASE_FIELDS;
  protected readonly stepFields = STEP_FIELDS;
  protected readonly priorities = PRIORITIES;
  protected readonly lifecycleLabel = $localize`:@@authoring.editor.lifecycle:Status`;
  protected readonly lifecycle = {
    draft: $localize`:@@authoring.editor.lifecycle-draft:Draft`,
    approved: $localize`:@@authoring.editor.lifecycle-approved:Approved`,
    retired: $localize`:@@authoring.editor.lifecycle-retired:Retired`,
  };
  protected readonly reasonUnavailable = REASON_UNAVAILABLE;
  protected readonly writeReason = WRITE_REASON;
  protected readonly count = codePoints;
  protected readonly notSet = $localize`:@@authoring.editor.not-set:Not set`;
  protected readonly errorOf = errorOf;
  protected readonly tab = signal('editor');
  protected readonly leaving = signal(false);
  protected readonly stepAnnouncement = signal('');
  private readonly focusStep = signal<number | null>(null);
  protected readonly title = computed(() =>
    this.status() === 'ready'
      ? (this.store.base()?.view.title ?? $localize`:@@authoring.editor.page-title:Case editor`)
      : $localize`:@@authoring.editor.page-title:Case editor`,
  );
  protected readonly intro = $localize`:@@authoring.editor.intro:Edit an existing manual case. Saving appends a version; past versions remain unchanged.`;
  protected readonly codeLabel = $localize`:@@authoring.editor.case-code:Case code`;
  protected readonly status = computed(() => {
    const error = errorOf(this.store.head.error());
    if (
      !this.store.scope() ||
      error?.kind === 'forbidden' ||
      error?.kind === 'not_found' ||
      error?.kind === 'unauthenticated'
    )
      return 'error';
    return this.store.base() ? 'ready' : this.store.head.error() ? 'error' : 'loading';
  });
  protected readonly blocked = computed(
    () =>
      !this.store.canWrite() ||
      !this.viewport.canMutate() ||
      this.store.saving() ||
      !this.store.retryWait.ready() ||
      !this.store.base()?.etag ||
      this.store.conflictOpen() ||
      this.store.base()?.view.state === 'retired',
  );
  protected readonly readonlyHeading = viewChild<ElementRef<HTMLElement>>('readonlyHeading');
  private readonly timeline = viewChild(TimelineComponent);
  private lastFocused: Element | null = null;
  private leaveDecision: ((answer: boolean) => void) | undefined;
  private readonly stepIds = computed(
    () =>
      this.store
        .draft()
        ?.steps.map((step) => step.id)
        .join(':') ?? '',
  );
  protected readonly feedback = computed(() => {
    const ids = this.stepIds().split(':').filter(Boolean);
    return createFormFeedback([
      ...CASE_FIELDS.map((field) => ({
        id: 'case-' + field.key,
        label: field.label,
        describedBy: 'case-' + field.key + '-hint',
        error: () => this.fieldError(field.key),
      })),
      {
        id: 'case-priority',
        label: $localize`:@@authoring.editor.priority:Priority`,
        error: () => this.pointerError('/priority'),
      },
      ...ids.flatMap((id, index) =>
        STEP_FIELDS.map((field) => ({
          id: 'case-step-' + index + '-' + field.key,
          label: $localize`:@@authoring.editor.step-field:Step ${index + 1}:step: · ${field.label}:field:`,
          describedBy: 'case-step-' + index + '-' + field.key + '-hint',
          error: () => this.stepError(id, field.key),
        })),
      ),
    ]);
  });
  protected readonly conflictFields = computed(() => {
    const draft = this.store.draft();
    const server = this.store.currentConflict()?.view;
    return [
      ...CASE_FIELDS.map((field) => ({
        id: field.key,
        label: field.label,
        local: draft?.[field.key] ?? null,
        server: server?.[field.key] ?? null,
      })),
      {
        id: 'priority',
        label: $localize`:@@authoring.editor.priority:Priority`,
        local: draft?.priority ?? null,
        server: server?.priority ?? null,
      },
      {
        id: 'steps',
        label: $localize`:@@authoring.editor.steps:Steps`,
        local: draft
          ? JSON.stringify(
              draft.steps.map(({ no, action, expected, data }) => ({ no, action, expected, data })),
              null,
              2,
            )
          : null,
        server: server ? JSON.stringify(server.steps, null, 2) : null,
      },
    ];
  });
  constructor() {
    this.destroy.onDestroy(() => this.leaveDecision?.(false));
    afterRenderEffect(() => {
      const narrow = this.viewport.narrow();
      if (narrow && this.lastFocused?.closest('[role="tablist"]'))
        this.document.getElementById('case-panel-select')?.focus();
      else if (narrow && this.lastFocused && !this.document.contains(this.lastFocused))
        this.readonlyHeading()?.nativeElement.focus();
      const step = this.focusStep();
      if (!narrow && step !== null) {
        this.document.getElementById('case-step-' + step + '-action')?.focus();
        this.focusStep.set(null);
      }
      this.lastFocused = this.host.nativeElement.contains(this.document.activeElement)
        ? this.document.activeElement
        : null;
    });
  }
  protected rememberFocus(event: FocusEvent): void {
    this.lastFocused = event.target instanceof Element ? event.target : null;
  }
  protected fieldError(key: CaseTextField): string | null {
    const field = CASE_FIELDS.find((entry) => entry.key === key);
    const draft = this.store.draft();
    return field && draft
      ? (textError(draft[key], field.limit, field.required) ?? this.pointerError('/' + key))
      : null;
  }
  protected stepError(id: string, key: StepTextField): string | null {
    const index = this.store.draft()?.steps.findIndex((step) => step.id === id) ?? -1;
    const step = this.store.draft()?.steps[index];
    return step
      ? (textError(step[key] ?? '', 2000, key !== 'data') ??
          this.pointerError('/steps/' + index + '/' + key))
      : null;
  }
  private pointerError(path: string): string | null {
    const failure = this.store.failure();
    return failure?.kind === 'validation' &&
      failure.details.some(
        (detail) =>
          detail.path === path || (path.startsWith('/steps/') && detail.path === '/steps'),
      )
      ? SERVER_FIELD_ERROR
      : null;
  }
  protected stepFeedback(index: number, field: number) {
    return this.feedback().fields[CASE_FIELDS.length + 1 + index * STEP_FIELDS.length + field];
  }
  protected priorityLabel(value: string): string {
    return PRIORITIES.find((priority) => priority.value === value)?.label ?? value;
  }
  /** The narrow panel select shows the tab's label, not the raw wire value it closes over. */
  protected readonly panelLabel = (value: string): string => PANEL_LABELS[value] ?? value;
  protected readonly panelSelectLabel = $localize`:@@authoring.editor.choose-panel:Choose a section`;
  protected readonly prioritySelectLabel = $localize`:@@authoring.editor.priority:Priority`;
  protected changePriority(value: unknown): void {
    const priority = PRIORITIES.find((entry) => entry.value === value);
    if (priority) this.store.editPriority(priority.value);
  }
  protected selectTab(value: unknown): void {
    if (value === 'editor' || value === 'timeline') this.tab.set(value);
  }
  protected moveStep(id: string, offset: -1 | 1): void {
    if (this.blocked() || !this.viewport.allowsMutation()) return;
    this.store.moveStep(id, offset);
    const index = this.store.draft()?.steps.findIndex((step) => step.id === id) ?? -1;
    if (index >= 0)
      this.stepAnnouncement.set(
        $localize`:@@authoring.editor.step-moved:Step moved to position ${index + 1}:position:.`,
      );
  }
  protected addStep(): void {
    if (this.blocked() || !this.viewport.allowsMutation()) return;
    const before = this.store.draft()?.steps.length ?? 0;
    this.store.addStep();
    if ((this.store.draft()?.steps.length ?? 0) > before) this.focusStep.set(before);
  }
  protected removeStep(id: string): void {
    if (this.blocked() || !this.viewport.allowsMutation()) return;
    const before = this.store.draft()?.steps.length ?? 0;
    const index = this.store.draft()?.steps.findIndex((step) => step.id === id) ?? -1;
    this.store.removeStep(id);
    if (before === (this.store.draft()?.steps.length ?? 0)) return;
    this.focusStep.set(Math.min(index, before - 2));
    this.stepAnnouncement.set(
      $localize`:@@authoring.editor.step-removed:Step removed. The remaining steps have been renumbered.`,
    );
  }
  protected async save(): Promise<void> {
    if (this.blocked() || !this.viewport.allowsMutation()) return;
    if (!this.feedback().submit()) return;
    const saved = await this.store.save();
    if (!saved && this.store.failure()?.kind === 'validation') this.feedback().submit();
  }
  protected beforeUnload(event: BeforeUnloadEvent): void {
    if (this.store.dirty()) {
      event.preventDefault();
      event.returnValue = '';
    }
  }
  confirmLeave(): Promise<boolean> {
    if (!this.store.dirty()) return Promise.resolve(true);
    if (this.store.conflictOpen() || this.timeline()?.hasOpenConfirmation())
      return Promise.resolve(false);
    if (this.leaveDecision) return Promise.resolve(false);
    this.leaving.set(true);
    return new Promise<boolean>((resolve) => {
      this.leaveDecision = resolve;
    });
  }
  protected resolveLeave(answer: boolean): void {
    this.leaving.set(false);
    this.leaveDecision?.(answer);
    this.leaveDecision = undefined;
  }
}
