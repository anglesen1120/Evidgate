import { DOCUMENT } from '@angular/common';
import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  signal,
  viewChild,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import type { AiSettingsTemplateView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardTitle } from '@evidgate/ui/card';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmTabsImports } from '@evidgate/ui/tabs';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { createFormFeedback } from '../../../shared/forms/form-feedback';
import { FieldFeedbackComponent } from '../../../shared/forms/field-feedback.component';
import { FormErrorSummaryComponent } from '../../../shared/forms/form-error-summary.component';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { DesktopRequiredNoticeComponent, NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { ConflictDialogComponent } from '../../../shared/ui/conflict/conflict-dialog.component';
import { isApiRequestError } from '../../../core/http/api-error';
import { AiSettingsHistoryComponent } from './ai-settings-history.component';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { CopyableIdentifierComponent } from '../../../shared/ui/values/copyable-identifier.component';
import { AiSettingsStore } from './ai-settings.store';
import { count, FIELDS, replaceText, textOf, type TextField } from './ai-settings.model';

function headingLevel(data: Record<string, unknown>): number {
  const level = data['pageHeadingLevel'];
  return typeof level === 'number' ? level : 1;
}

@Component({
  selector: 'app-ai-settings-page',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    FormsModule,
    HlmButton,
    HlmCardTitle,
    HlmInput,
    HlmLabel,
    HlmCheckbox,
    HlmTabsImports,
    HlmDialogImports,
    FieldFeedbackComponent,
    FormErrorSummaryComponent,
    PageHeaderComponent,
    DesktopRequiredNoticeComponent,
    RequestStateComponent,
    ConflictDialogComponent,
    AiSettingsHistoryComponent,
    AbsoluteTimeComponent,
    CopyableIdentifierComponent,
  ],
  providers: [AiSettingsStore],
  templateUrl: './ai-settings.page.html',
  host: { class: 'block min-w-0 text-foreground', '(focusin)': 'rememberFocus($event)' },
})
export class AiSettingsPage {
  protected readonly store = inject(AiSettingsStore);
  protected readonly viewport = inject(NarrowMutationPolicy);
  private readonly document = inject(DOCUMENT);
  private readonly route = inject(ActivatedRoute);
  private readonly host = inject<ElementRef<HTMLElement>>(ElementRef);
  protected readonly pageHeadingLevel = headingLevel(this.route.snapshot.data);
  protected readonly fields = FIELDS;
  protected readonly languages: readonly ('en' | 'vi')[] = ['en', 'vi'];
  protected readonly count = count;
  protected readonly textOf = textOf;
  protected readonly composition = signal<ReadonlySet<TextField>>(new Set());
  protected readonly announcement = signal('');
  protected readonly preview = signal<AiSettingsTemplateView | null>(null);
  protected readonly title = $localize`:@@authoring.ai-settings.title:AI settings`;
  protected readonly description = $localize`:@@authoring.ai-settings.intro:Project guidance for authored cases. Saving creates an immutable settings version.`;
  protected readonly authorLabel = $localize`:@@authoring.ai-settings.author:Author membership`;
  private templateOpener: HTMLElement | null = null;
  private readonly compositionBands = new Map<TextField, number>();
  private readonly readOnlyHeading = viewChild<ElementRef<HTMLElement>>('readOnlyHeading');
  private lastFocused: Element | null = null;
  protected readonly status = computed(() => {
    if (!this.store.scope()) return 'error';
    const error = this.store.error();
    if (
      error?.kind === 'forbidden' ||
      error?.kind === 'not_found' ||
      error?.kind === 'unauthenticated'
    )
      return 'error';
    return this.store.base() ? 'ready' : this.store.head.error() ? 'error' : 'loading';
  });
  protected readonly conflictFields = computed(() => {
    const draft = this.store.draft();
    const server = this.store.currentConflict()?.view.settings;
    const fields = FIELDS.filter((field) => field.key !== 'reason').map((field) => ({
      id: field.key,
      label: field.label,
      local: draft ? textOf(draft, field.key) : null,
      server: server ? textOf(server, field.key) : null,
    }));
    return [
      ...fields,
      {
        id: 'languages',
        label: $localize`:@@authoring.ai-settings.languages:Output languages`,
        local: draft?.languages.join(', ') ?? null,
        server: server?.languages.join(', ') ?? null,
      },
    ];
  });
  protected readonly conflictError = computed(() => {
    const error = this.store.conflict.error();
    return isApiRequestError(error) ? error.error : null;
  });
  protected readonly templateError = computed(() => {
    const error = this.store.templates.error();
    return isApiRequestError(error) ? error.error : null;
  });
  protected languageError(): string | null {
    if (!this.store.draft()?.languages.length)
      return $localize`:@@authoring.ai-settings.language-required:Select at least one output language.`;
    return this.store
      .failure()
      ?.details.some((entry) => entry.path?.startsWith('/settings/languages'))
      ? $localize`:@@authoring.ai-settings.server-field:The server refused this field. Review its value before saving.`
      : null;
  }
  protected readonly feedback = createFormFeedback([
    ...FIELDS.map((field) => ({
      id: 'ai-' + field.key,
      label: field.label,
      describedBy: 'ai-' + field.key + '-counter',
      error: () => this.fieldError(field.key),
    })),
    {
      id: 'ai-languages',
      label: $localize`:@@authoring.ai-settings.languages:Output languages`,
      error: () => this.languageError(),
    },
  ]);
  protected readonly invalid = computed(
    () =>
      FIELDS.some((field) => this.fieldError(field.key) !== null) ||
      this.languageError() !== null ||
      this.composition().size > 0,
  );
  protected readonly blocked = computed(
    () =>
      !this.store.canWrite() ||
      !this.viewport.canMutate() ||
      this.store.saving() ||
      !this.store.retryWait.ready() ||
      !this.store.base()?.etag ||
      this.invalid(),
  );
  protected rememberFocus(event: FocusEvent): void {
    this.lastFocused = event.target instanceof Element ? event.target : null;
  }
  constructor() {
    afterRenderEffect(() => {
      const narrow = this.viewport.narrow();
      if (narrow && this.lastFocused && !this.document.contains(this.lastFocused))
        this.readOnlyHeading()?.nativeElement.focus();
      if (narrow && this.preview()) {
        this.preview.set(null);
        this.readOnlyHeading()?.nativeElement.focus();
      }
      this.lastFocused = this.host.nativeElement.contains(this.document.activeElement)
        ? this.document.activeElement
        : null;
    });
  }
  protected value(key: TextField): string {
    const draft = this.store.draft();
    return key === 'reason' ? this.store.reason() : draft ? textOf(draft, key) : '';
  }
  protected fieldError(key: TextField): string | null {
    if (this.composition().has(key)) return null;
    const field = FIELDS.find((entry) => entry.key === key);
    if (field && count(this.value(key)) > field.limit)
      return $localize`:@@authoring.ai-settings.over-limit:Shorten this field before saving.`;
    if (key === 'environment_url' && this.value(key).trim()) {
      try {
        const url = new URL(this.value(key).normalize('NFC').trim());
        if (!['https:', 'http:'].includes(url.protocol) || url.username || url.password)
          return $localize`:@@authoring.ai-settings.url-error:Enter an absolute HTTP or HTTPS URL without credentials.`;
      } catch {
        return $localize`:@@authoring.ai-settings.url-error:Enter an absolute HTTP or HTTPS URL without credentials.`;
      }
    }
    const failure = this.store.failure();
    if (
      failure?.kind === 'validation' &&
      failure.details.some(
        (detail) =>
          (detail.path === '/reason' && key === 'reason') ||
          detail.path?.replace('/settings/priority_criteria/', '').replace('/settings/', '') ===
            key,
      )
    )
      return $localize`:@@authoring.ai-settings.server-field:The server refused this field. Review its value before saving.`;
    return null;
  }
  protected edit(key: TextField, value: string): void {
    const before = this.band(key, this.value(key));
    if (key === 'reason') this.store.setReason(value);
    else {
      const draft = this.store.draft();
      if (draft) this.store.edit(replaceText(draft, key, value));
    }
    const after = this.band(key, value);
    if (!this.composition().has(key) && before !== after) this.announceBoundary(after);
  }
  protected compose(key: TextField, active: boolean): void {
    const keys = new Set(this.composition());
    if (active) {
      this.compositionBands.set(key, this.band(key, this.value(key)));
      keys.add(key);
    } else {
      keys.delete(key);
      const band = this.band(key, this.value(key));
      if (this.compositionBands.get(key) !== band) this.announceBoundary(band);
      this.compositionBands.delete(key);
    }
    this.composition.set(keys);
  }
  private band(key: TextField, value: string): number {
    const limit = FIELDS.find((field) => field.key === key)?.limit ?? 0;
    return Math.sign(count(value) - limit);
  }
  private announceBoundary(band: number): void {
    this.announcement.set(
      band > 0
        ? $localize`:@@authoring.ai-settings.over-announcement:Character limit exceeded. Shorten the field before saving.`
        : band === 0
          ? $localize`:@@authoring.ai-settings.at-announcement:Character limit reached.`
          : $localize`:@@authoring.ai-settings.within-announcement:Within the character limit.`,
    );
  }
  protected language(language: 'en' | 'vi', checked: boolean): void {
    const draft = this.store.draft();
    if (!draft) return;
    const languages = checked
      ? [...new Set([...draft.languages, language])].sort()
      : draft.languages.filter((value) => value !== language);
    this.store.edit({ ...draft, languages });
    this.feedback.fields[FIELDS.length]?.blur();
  }
  protected async save(): Promise<void> {
    if (!this.store.canWrite() || !this.viewport.allowsMutation() || this.store.saving()) return;
    if (!this.feedback.submit() || this.invalid()) return;
    const success = await this.store.save();
    if (!success && this.store.failure()?.kind === 'validation') this.feedback.submit();
  }
  protected previewTemplate(template: AiSettingsTemplateView, event: MouseEvent): void {
    if (!this.viewport.allowsMutation() || !this.store.canWrite()) return;
    this.templateOpener = event.currentTarget instanceof HTMLElement ? event.currentTarget : null;
    this.preview.set(template);
  }
  protected templateClosed(): void {
    if (this.viewport.narrow()) this.readOnlyHeading()?.nativeElement.focus();
    else if (this.templateOpener?.isConnected) this.templateOpener.focus();
  }
  protected replaceTemplate(): void {
    const template = this.preview();
    if (!template || !this.viewport.allowsMutation() || !this.store.canWrite()) return;
    this.store.useTemplate(template.settings, { code: template.code, version: template.version });
    this.preview.set(null);
    this.feedback.reset();
  }
}
