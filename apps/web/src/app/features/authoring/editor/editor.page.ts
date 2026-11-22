import { DOCUMENT, NgTemplateOutlet } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  linkedSignal,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTabsImports } from '@evidgate/ui/tabs';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { createFormFeedback } from '../../../shared/forms/form-feedback';
import { FieldFeedbackComponent } from '../../../shared/forms/field-feedback.component';
import { FormErrorSummaryComponent } from '../../../shared/forms/form-error-summary.component';
import {
  FieldRequirementComponent,
  FormRequiredNoteComponent,
} from '../../../shared/forms/form-requirement.component';
import { ConflictDialogComponent } from '../../../shared/ui/conflict/conflict-dialog.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { DesktopRequiredNoticeComponent, NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { ChecklistReviewTabComponent } from '../pending-changes/checklist-review-tab.component';
import { EditorReadonlyValuesComponent } from './editor-readonly-values.component';
import { EditorRecordSummaryComponent } from './editor-record-summary.component';
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

/** Conflict comparison must show stored values, so an omitted optional field equals an explicit null. */
function stepsSnapshot(
  steps: readonly {
    readonly no: number;
    readonly action: string;
    readonly expected: string;
    readonly data?: string | null;
  }[],
): string {
  return JSON.stringify(
    steps.map(({ no, action, expected, data }) => ({ no, action, expected, data: data ?? null })),
    null,
    2,
  );
}

@Component({
  selector: 'app-authoring-editor',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    NgTemplateOutlet,
    HlmButton,
    HlmCard,
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
    ConflictDialogComponent,
    PageHeaderComponent,
    DesktopRequiredNoticeComponent,
    RequestStateComponent,
    EditorReadonlyValuesComponent,
    EditorRecordSummaryComponent,
    ShellIconComponent,
    TimelineComponent,
    ChecklistReviewTabComponent,
  ],
  providers: [EditorStore],
  templateUrl: './editor.page.html',
  styleUrl: './editor.page.css',
  host: {
    class: 'block min-w-0 text-foreground',
    '(focusin)': 'rememberFocus($event)',
    '(window:resize)': 'revealActiveTab()',
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
  protected readonly reasonUnavailable = REASON_UNAVAILABLE;
  protected readonly writeReason = WRITE_REASON;
  protected readonly count = codePoints;
  protected readonly errorOf = errorOf;
  protected readonly tab = linkedSignal({ source: this.store.scope, computation: () => 'editor' });
  protected readonly reviewVisited = linkedSignal({
    source: this.store.scope,
    computation: () => false,
  });
  protected readonly leaving = signal(false);
  protected readonly stepAnnouncement = signal('');
  private readonly focusStep = signal<number | null>(null);
  protected readonly title = computed(() =>
    this.status() === 'ready'
      ? (this.store.base()?.view.title ?? $localize`:@@authoring.editor.page-title:Case editor`)
      : $localize`:@@authoring.editor.page-title:Case editor`,
  );
  protected readonly intro = computed(() => {
    const view = this.store.base()?.view;
    return this.status() === 'ready' && view
      ? `${view.code} · ${$localize`:@@authoring.editor.version-column:Version`}: ${view.version}`
      : $localize`:@@authoring.editor.intro:Edit an existing manual case. Saving appends a version; past versions remain unchanged.`;
  });
  protected readonly contentTitle = $localize`:@@authoring.editor.case-content:Case content`;
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
      this.store.locked() ||
      !this.store.retryWait.ready() ||
      !this.store.base()?.etag ||
      this.store.conflictOpen() ||
      this.store.base()?.view.state === 'retired',
  );
  protected readonly readonlyHeading = viewChild<ElementRef<HTMLElement>>('readonlyHeading');
  private readonly timeline = viewChild(TimelineComponent);
  private readonly tabStrip = viewChild<ElementRef<HTMLElement>>('tabStrip');
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
        local: draft ? stepsSnapshot(draft.steps) : null,
        server: server ? stepsSnapshot(server.steps) : null,
      },
    ];
  });
  constructor() {
    this.destroy.onDestroy(() => this.leaveDecision?.(false));
    afterRenderEffect(() => {
      this.tab();
      this.revealActiveTab();
    });
    afterRenderEffect(() => {
      const narrow = this.viewport.narrow();
      if (narrow && this.lastFocused && !this.document.contains(this.lastFocused))
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
  protected revealActiveTab(): void {
    const strip = this.tabStrip()?.nativeElement;
    if (strip) this.revealTab(strip, strip.querySelector('[aria-selected="true"]'));
  }
  protected revealTab(strip: HTMLElement, target: EventTarget | null): void {
    if (!(target instanceof HTMLElement) || strip.scrollWidth <= strip.clientWidth) return;
    const bounds = strip.getBoundingClientRect();
    const tab = target.getBoundingClientRect();
    const inset = parseFloat(getComputedStyle(strip).paddingInlineStart) || 0;
    if (tab.left < bounds.left + inset) strip.scrollLeft += tab.left - bounds.left - inset;
    else if (tab.right > bounds.right - inset) strip.scrollLeft += tab.right - bounds.right + inset;
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
  protected readonly prioritySelectLabel = $localize`:@@authoring.editor.priority:Priority`;
  protected changePriority(value: unknown): void {
    const priority = PRIORITIES.find((entry) => entry.value === value);
    if (priority) this.store.editPriority(priority.value);
  }
  protected selectTab(value: unknown): void {
    if (value === 'editor' || value === 'review' || value === 'timeline') {
      if (value === 'review') this.reviewVisited.set(true);
      this.tab.set(value);
    }
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
