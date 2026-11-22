import { DOCUMENT } from '@angular/common';
import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmDialog, HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmLabel } from '@evidgate/ui/label';
import { StatusPillComponent } from '../chips';
import { NarrowMutationPolicy } from '../narrow/narrow-mutation-policy';
import { MEMBER_COPY, type GalleryMember } from './gallery-member-samples';

type Mode = 'view' | 'edit';

@Component({
  selector: 'app-gallery-member-dialog',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmButton, HlmCheckbox, HlmDialogImports, HlmLabel, StatusPillComponent],
  templateUrl: './gallery-member-dialog.component.html',
})
export class GalleryMemberDialogComponent {
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  protected readonly policy = inject(NarrowMutationPolicy);
  readonly rolesApplied = output<GalleryMember>();
  protected readonly copy = MEMBER_COPY;
  protected readonly member = signal<GalleryMember | null>(null);
  protected readonly mode = signal<Mode>('view');
  protected readonly reviewer = signal(false);
  protected readonly confirming = signal(false);
  protected readonly dirty = computed(
    () => this.mode() === 'edit' && this.member()?.reviewer !== this.reviewer(),
  );
  protected opener: HTMLElement | null = null;
  private fallback: HTMLElement | null = null;
  private applied: GalleryMember | null = null;
  private editFocusId = '';
  private readonly content = viewChild<ElementRef<HTMLElement>, ElementRef<HTMLElement>>(
    'content',
    {
      read: ElementRef,
    },
  );
  private readonly dialog = viewChild(HlmDialog);

  open(member: GalleryMember, mode: Mode, opener: HTMLElement, fallback: HTMLElement): void {
    if (this.member() || (mode === 'edit' && !this.policy.allowsMutation())) return;
    this.opener = opener;
    this.fallback = fallback;
    this.mode.set(mode);
    this.reviewer.set(member.reviewer);
    this.member.set(member);
  }

  protected edit(): void {
    if (!this.policy.allowsMutation()) return;
    this.mode.set('edit');
    this.focusAfterRender('[role="checkbox"]');
  }

  // All dismissals, including the overlay's Close, backdrop and Escape, consult the same draft.
  protected readonly canClose = (): boolean => {
    if (!this.dirty()) return true;
    if (!this.confirming()) {
      this.editFocusId = this.document.activeElement?.id ?? '';
      this.confirming.set(true);
      this.focusAfterRender('[data-keep]');
    }
    return false;
  };

  protected keepEditing(): void {
    this.confirming.set(false);
    const id = this.editFocusId;
    afterNextRender(
      () => {
        const previous = id ? this.document.getElementById(id) : null;
        const content = this.content()?.nativeElement;
        if (
          previous &&
          content?.contains(previous) &&
          !previous.matches(':disabled, [aria-disabled="true"]')
        ) {
          previous.focus();
        } else {
          const selector = this.policy.narrow() ? '#gallery-member-cancel' : '[role="checkbox"]';
          content?.querySelector<HTMLElement>(selector)?.focus();
        }
      },
      { injector: this.injector },
    );
  }

  protected apply(): void {
    const member = this.member();
    if (!member || !this.dirty() || this.confirming() || !this.policy.allowsMutation()) return;
    const updated = { ...member, reviewer: this.reviewer() };
    this.member.set(updated);
    this.applied = updated;
    this.dialog()?.close();
  }

  protected discard(): void {
    const member = this.member();
    if (!member || !this.confirming()) return;
    this.reviewer.set(member.reviewer);
    this.dialog()?.close();
  }

  protected closed(): void {
    if (!this.opener?.isConnected && this.fallback?.isConnected) this.fallback.focus();
    if (this.applied) this.rolesApplied.emit(this.applied);
    this.applied = null;
    this.member.set(null);
    this.confirming.set(false);
    this.opener = this.fallback = null;
  }

  private focusAfterRender(selector: string): void {
    afterNextRender(
      () => this.content()?.nativeElement.querySelector<HTMLElement>(selector)?.focus(),
      { injector: this.injector },
    );
  }
}
