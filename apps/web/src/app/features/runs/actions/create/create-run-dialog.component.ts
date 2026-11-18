import { DOCUMENT } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  ElementRef,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { ActivatedRoute, Router } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { SessionStore } from '../../../../core/session/session.store';
import {
  createFormFeedback,
  FieldFeedbackComponent,
  FormErrorSummaryComponent,
  FormRequiredNoteComponent,
  type FieldFeedback,
} from '../../../../shared/forms';
import { NarrowMutationPolicy } from '../../../../shared/ui/narrow';
import { InlineFeedbackComponent } from '../../../../shared/ui/states/inline-feedback.component';
import { CREATE_RUN_COPY } from './create-run.copy';
import {
  CREATE_RUN_TEXT_FIELDS,
  EMPTY_CREATE_RUN_DRAFT,
  createRunBody,
  createRunFieldError,
  createRunRefusal,
  type CreateRunDraft,
  type CreateRunField,
  type CreateRunTextField,
} from './create-run-form';
import { CreateRunStore } from './create-run.store';

@Component({
  selector: 'app-create-run-dialog',
  imports: [
    HlmButton,
    HlmCheckbox,
    HlmDialogImports,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    FormRequiredNoteComponent,
    InlineFeedbackComponent,
  ],
  providers: [CreateRunStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './create-run-dialog.component.html',
})
export class CreateRunDialogComponent {
  protected readonly store = inject(CreateRunStore);
  protected readonly viewport = inject(NarrowMutationPolicy);
  private readonly session = inject(SessionStore);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly document = inject(DOCUMENT);
  private readonly destroyRef = inject(DestroyRef);
  readonly projectId = input.required<string>();
  readonly closed = output<void>();
  protected readonly copy = CREATE_RUN_COPY;
  protected readonly opened = signal(true);
  protected readonly draft = signal<CreateRunDraft>({ ...EMPTY_CREATE_RUN_DRAFT });
  private readonly openingOrg = this.session.currentOrg()?.id;
  protected readonly scopeMatches = computed(
    () =>
      this.openingOrg !== undefined &&
      this.session.currentOrg()?.id === this.openingOrg &&
      this.session.currentProject()?.id === this.projectId(),
  );
  protected readonly canCreate = computed(() => this.session.has('run:create'));
  protected readonly locked = computed(
    () =>
      this.store.submitting() ||
      !this.canCreate() ||
      !this.scopeMatches() ||
      !this.viewport.canMutate(),
  );
  protected readonly refusal = computed(() => {
    const error = this.store.error();
    return error ? createRunRefusal(error) : '';
  });
  protected readonly feedback = createFormFeedback([
    ...CREATE_RUN_TEXT_FIELDS.map((field) => ({
      id: `run-create-${field.key}`,
      label: field.label,
      hint: field.hint,
      error: () => createRunFieldError(field.key, this.draft(), this.store.error()),
    })),
    ...(
      [
        { key: 'purpose', label: this.copy.purpose },
        { key: 'kind', label: this.copy.kind },
        { key: 'triggerEvent', label: this.copy.trigger },
        { key: 'isFork', label: this.copy.fork },
      ] as const
    ).map((field) => ({
      id: `run-create-${field.key}`,
      label: field.label,
      error: () => createRunFieldError(field.key, this.draft(), this.store.error()),
    })),
  ]);
  protected readonly basicFields = CREATE_RUN_TEXT_FIELDS.slice(0, 2);
  protected readonly refFields = CREATE_RUN_TEXT_FIELDS.slice(3, 7);
  protected readonly refExpanded = signal(false);
  protected readonly refInvalid = computed(() =>
    ['refName', 'prNumber', 'headSha', 'baseSha', 'kind', 'triggerEvent', 'isFork'].some((key) =>
      this.feedback.fields.find((entry) => entry.id === `run-create-${key}`)?.invalid(),
    ),
  );
  protected readonly purposeOptions = [
    { value: 'regular', label: this.copy.regular },
    { value: 'replay', label: this.copy.replay },
  ];
  protected readonly kindOptions = [
    { value: '', label: this.copy.none },
    { value: 'branch', label: this.copy.branch },
    { value: 'tag', label: this.copy.tag },
    { value: 'pull_request', label: this.copy.pullRequest },
  ];
  protected readonly triggerOptions = [
    { value: '', label: this.copy.none },
    { value: 'push', label: this.copy.push },
    { value: 'pull_request', label: this.copy.pullRequest },
    { value: 'schedule', label: this.copy.schedule },
    { value: 'manual', label: this.copy.manual },
    { value: 'dispatch', label: this.copy.dispatch },
  ];
  protected readonly purposeLabel = (value: unknown) =>
    this.purposeOptions.find((o) => o.value === value)?.label ?? this.copy.regular;
  protected readonly kindLabel = (value: unknown) =>
    this.kindOptions.find((o) => o.value === value)?.label ?? this.copy.none;
  protected readonly triggerLabel = (value: unknown) =>
    this.triggerOptions.find((o) => o.value === value)?.label ?? this.copy.none;
  private readonly manifestControl = viewChild<ElementRef<HTMLInputElement>>('manifestControl');
  private readonly opener =
    this.document.activeElement instanceof HTMLElement ? this.document.activeElement : null;
  private readonly request = new AbortController();
  private focused = false;
  private finished = false;
  private createdRunId: string | null = null;

  constructor() {
    const off = this.session.onSessionEnd(() => this.opened.set(false));
    this.destroyRef.onDestroy(() => {
      off();
      this.request.abort();
    });
    afterRenderEffect(() => {
      const control = this.manifestControl()?.nativeElement;
      if (control && !this.focused) {
        control.focus();
        this.focused = true;
      }
    });
  }

  protected field(key: CreateRunField): FieldFeedback {
    const field = this.feedback.fields.find((entry) => entry.id === `run-create-${key}`);
    if (!field) throw new Error(`Missing form feedback for ${key}`);
    return field;
  }
  protected update(key: CreateRunTextField, event: Event): void {
    if (this.locked()) return;
    const target = event.target;
    if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement)) return;
    this.draft.update((current) => ({ ...current, [key]: target.value }));
    this.store.clearError();
  }
  protected choose(key: 'purpose' | 'kind' | 'triggerEvent', value: unknown): void {
    if (this.locked() || typeof value !== 'string') return;
    const options =
      key === 'purpose'
        ? this.purposeOptions
        : key === 'kind'
          ? this.kindOptions
          : this.triggerOptions;
    if (!options.some((option) => option.value === value)) return;
    this.draft.update((current) => ({
      ...current,
      [key]: value,
      ...(key === 'purpose' && value === 'regular' ? { replayGrantId: '' } : {}),
    }));
    this.store.clearError();
  }
  protected fork(value: boolean): void {
    if (this.locked()) return;
    this.draft.update((current) => ({ ...current, isFork: value }));
    this.store.clearError();
  }
  protected async submit(event: Event): Promise<void> {
    event.preventDefault();
    if (
      !this.opened() ||
      this.store.submitting() ||
      !this.scopeMatches() ||
      !this.canCreate() ||
      !this.viewport.allowsMutation()
    )
      return;
    this.store.clearError();
    if (!this.feedback.submit()) {
      if (this.refInvalid()) this.refExpanded.set(true);
      return;
    }
    const created = await this.store.create(
      this.projectId(),
      createRunBody(this.draft()),
      this.request.signal,
    );
    if (this.destroyRef.destroyed || !this.opened() || !this.scopeMatches()) return;
    if (created) {
      this.createdRunId = created.run_id;
      this.opened.set(false);
    } else {
      this.feedback.submit();
      if (this.refInvalid()) this.refExpanded.set(true);
    }
  }
  protected dismiss(): void {
    if (!this.store.submitting()) this.opened.set(false);
  }
  protected escape(event: Event): void {
    event.preventDefault();
    event.stopPropagation();
    this.dismiss();
  }
  protected finishClose(): void {
    if (this.finished) return;
    this.finished = true;
    if (this.opener?.isConnected && !this.opener.matches(':disabled')) this.opener.focus();
    else this.document.querySelector<HTMLElement>('app-run-list-page app-page-header')?.focus();
    const runId = this.createdRunId;
    if (runId && this.scopeMatches())
      void this.router.navigate([runId], { relativeTo: this.route, queryParams: {} });
    this.closed.emit();
  }
}
