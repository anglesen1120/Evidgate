import { FieldHintComponent } from '../../shared/ui/forms/field-hint.component';
import { DEFAULT_DIALOG_CONFIG, Dialog, DialogConfig } from '@angular/cdk/dialog';
import { BrnDialogService } from '@spartan-ng/brain/dialog';
import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  DestroyRef,
  inject,
  Injector,
  input,
  linkedSignal,
  output,
  signal,
  untracked,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { CaseDto, MembershipView } from '@evidgate/sdk';
import { HlmButton } from '@evidgate/ui/button';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { StatusPillComponent } from '../../shared/ui/chips/status-pill.component';
import { escapeFormatChars } from '../../shared/ui/values/ref-name-display';
import { NarrowMutationPolicy } from '../../shared/ui/narrow/narrow-mutation-policy';
import { CaseRecordFactsComponent } from './case-record-facts.component';
import { CATALOG_RECORD } from './case-record.copy';

export type CatalogRecordMode = 'view' | 'edit';
export interface CatalogSelection {
  readonly id: string;
  readonly mode: CatalogRecordMode;
}
export type SaveCatalogOwner = (row: CaseDto, membershipId: string) => Promise<boolean>;

@Component({
  selector: 'app-case-record-dialog',
  imports: [
    FieldHintComponent,
    FormsModule,
    HlmButton,
    HlmDialogImports,
    HlmIcon,
    HlmLabel,
    HlmSelectImports,
    HlmTooltip,
    StatusPillComponent,
    CaseRecordFactsComponent,
  ],
  // The CDK default disposes on popstate, before Router can ask about an unsaved draft.
  // Scope this override to this overlay; neither app-wide dialog policy nor focus trapping changes.
  providers: [
    Dialog,
    BrnDialogService,
    {
      provide: DEFAULT_DIALOG_CONFIG,
      useFactory: () => ({ ...new DialogConfig(), closeOnNavigation: false }),
    },
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './case-record-dialog.component.html',
  styleUrl: './case-record-dialog.component.css',
  host: { '(window:beforeunload)': 'beforeUnload($event)' },
})
export class CaseRecordDialogComponent {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly destroyRef = inject(DestroyRef);
  private readonly viewport = inject(NarrowMutationPolicy);
  readonly row = input.required<CaseDto>();
  readonly mode = input.required<CatalogRecordMode>();
  readonly eligible = input.required<boolean>();
  readonly ownerHelp = input.required<string>();
  readonly members = input.required<ReadonlyMap<string, MembershipView>>();
  readonly choices = input.required<readonly MembershipView[]>();
  readonly busy = input.required<boolean>();
  readonly failure = input.required<string | null>();
  readonly saveOwner = input.required<SaveCatalogOwner | null>();
  readonly closeRecord = output<void>();
  readonly editRecord = output<void>();
  readonly reloadOwner = input<((id: string) => Promise<void>) | undefined>();
  protected readonly copy = CATALOG_RECORD;
  protected readonly display = escapeFormatChars;
  protected readonly editing = computed(() => this.mode() === 'edit' && this.eligible());
  private readonly editKey = computed(() => (this.editing() ? this.row().id : null));
  private readonly baseline = linkedSignal(() => {
    this.editKey();
    const row = untracked(this.row);
    return { owner: row.owner_membership_id ?? '', version: row.version };
  });
  protected readonly draft = linkedSignal(() => this.baseline().owner);
  protected readonly saving = signal(false);
  protected readonly confirming = linkedSignal(() => {
    this.editKey();
    return false;
  });
  private afterDiscard: (() => void) | null = null;
  protected readonly ownerName = computed(
    () => this.members().get(this.row().owner_membership_id ?? '')?.invited_email ?? null,
  );
  protected readonly choiceLabel = (id: unknown) =>
    typeof id === 'string' && id
      ? this.display(this.members().get(id)?.invited_email ?? id)
      : this.copy.unassigned;
  private readonly dirty = computed(() => this.editing() && this.draft() !== this.baseline().owner);
  protected readonly canSave = computed(
    () =>
      this.dirty() &&
      !this.busy() &&
      !this.saving() &&
      !this.failure() &&
      this.baseline().version === this.row().version &&
      this.members().get(this.draft())?.status === 'active',
  );

  requestLeave(leave: () => void = () => this.closeRecord.emit()): boolean {
    if (this.busy() || this.saving()) return false;
    if (!this.dirty()) {
      leave();
      return true;
    }
    this.afterDiscard = leave;
    this.confirming.set(true);
    this.focus('catalog-record-keep');
    return false;
  }
  protected keepEditing(): void {
    this.afterDiscard = null;
    this.confirming.set(false);
    this.focus('catalog-record-owner');
  }
  protected discard(): void {
    const leave = this.afterDiscard;
    this.afterDiscard = null;
    this.draft.set(this.row().owner_membership_id ?? '');
    this.confirming.set(false);
    leave?.();
  }
  protected readonly allowClose = (): boolean => !this.confirming() && this.requestLeave();

  protected escape(event: Event): void {
    if (event.defaultPrevented || !this.confirming()) return;
    event.preventDefault();
    event.stopPropagation();
    this.keepEditing();
  }
  protected beforeUnload(event: BeforeUnloadEvent): void {
    if (this.dirty()) event.preventDefault();
  }
  protected startEdit(): void {
    if (!this.eligible() || this.busy()) return;
    this.editRecord.emit();
    this.focus('catalog-record-owner');
  }
  protected choose(value: unknown): void {
    if (
      !this.saving() &&
      typeof value === 'string' &&
      this.members().get(value)?.status === 'active'
    )
      this.draft.set(value);
  }
  protected async reload(): Promise<void> {
    const reload = this.reloadOwner();
    if (!reload || this.busy() || this.saving()) return;
    const key = this.editKey();
    await reload(this.row().id);
    if (this.destroyRef.destroyed) return;
    afterNextRender(
      () => {
        if (this.editKey() !== key || this.failure()) return;
        const row = this.row();
        this.baseline.set({ owner: row.owner_membership_id ?? '', version: row.version });
        this.draft.set(row.owner_membership_id ?? '');
        this.document.getElementById('catalog-record-owner')?.focus();
      },
      { injector: this.injector },
    );
  }
  protected async save(): Promise<void> {
    const setter = this.saveOwner();
    if (!setter || !this.viewport.allowsMutation() || !this.canSave()) return;
    this.saving.set(true);
    try {
      const success = await setter(this.row(), this.draft());
      if (this.destroyRef.destroyed) return;
      if (success) this.closeRecord.emit();
      else this.focus('catalog-record-error');
    } finally {
      this.saving.set(false);
    }
  }
  private focus(id: string): void {
    afterNextRender(() => this.document.getElementById(id)?.focus(), { injector: this.injector });
  }
}
