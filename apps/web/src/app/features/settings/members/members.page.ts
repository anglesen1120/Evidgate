import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import type { MembershipView } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { InviteDialogComponent } from './invite-dialog.component';
import { MemberListComponent, MEMBERS_COPY } from './member-list.component';
import { MembersStore } from './members.store';
import { RemoveDialogComponent } from './remove-dialog.component';
import { RoleMatrixComponent } from './role-matrix.component';

/** Org-scoped in both the Settings tab and the Members sidebar destination. */
@Component({
  selector: 'app-members-page',
  imports: [
    RequestStateComponent,
    MemberListComponent,
    RoleMatrixComponent,
    InviteDialogComponent,
    RemoveDialogComponent,
  ],
  providers: [MembersStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './members.page.html',
  styleUrl: './members.page.css',
  host: { '[attr.data-members-phase]': 'store.phase()' },
})
export class MembersPage {
  protected readonly store = inject(MembersStore);
  protected readonly session = inject(SessionStore);
  protected readonly copy = MEMBERS_COPY;
  protected readonly canWrite = computed(() => this.session.has('membership:write'));
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    const phase = this.store.phase();
    return phase === 'idle' ? 'loading' : phase;
  });
  // Keep variable-height semantic rows bounded; cursor pages remain explicit, never load all.
  protected readonly offset = signal(0);
  protected readonly visibleMembers = computed(() =>
    this.store.members().slice(this.offset(), this.offset() + 200),
  );
  protected readonly visibleMatrix = computed(() => {
    const ids = new Set(this.visibleMembers().map((member) => member.id));
    const matrix = this.store.matrix();
    return {
      columns: matrix.columns,
      rows: matrix.rows.filter((row) => ids.has(row.membership.id)),
    };
  });
  protected readonly more = computed(
    () => this.offset() + 200 < this.store.members().length || this.store.hasMore(),
  );
  protected readonly paging = signal(false);
  protected readonly saved = signal(false);
  protected readonly retryReady = signal(true);
  private readonly heading = viewChild.required<ElementRef<HTMLHeadingElement>>('heading');
  private readonly inviteDialog = viewChild(InviteDialogComponent);
  private readonly removeDialog = viewChild(RemoveDialogComponent);

  constructor() {
    effect(() => {
      const org = this.session.currentOrg();
      this.offset.set(0);
      this.saved.set(false);
      untracked(() => {
        this.inviteDialog()?.close();
        this.removeDialog()?.close();
        if (org) void this.store.load(org.id);
      });
    });
    effect((onCleanup) => {
      const seconds = this.store.loadError()?.retryAfterSeconds;
      this.retryReady.set(seconds == null || seconds <= 0);
      if (seconds != null && seconds > 0) {
        const timer = setTimeout(() => this.retryReady.set(true), seconds * 1000);
        onCleanup(() => clearTimeout(timer));
      }
    });
  }

  protected reload(): void {
    const org = this.session.currentOrg();
    if (org) {
      this.offset.set(0);
      void this.store.load(org.id);
    }
  }
  protected openInvite(event: Event): void {
    if (this.canWrite() && event.currentTarget instanceof HTMLElement)
      this.inviteDialog()?.open(event.currentTarget);
  }
  protected openRemove(event: { member: MembershipView; opener: HTMLElement }): void {
    if (this.canWrite()) this.removeDialog()?.open(event.member, event.opener);
  }
  protected completed(removed = false): void {
    this.saved.set(true);
    this.offset.set(0);
    if (removed) this.heading().nativeElement.focus();
  }
  protected previous(): void {
    this.offset.update((offset) => Math.max(0, offset - 200));
    this.heading().nativeElement.focus();
  }
  protected async next(): Promise<void> {
    if (this.paging()) return;
    this.paging.set(true);
    try {
      if (this.offset() + 200 >= this.store.members().length) await this.store.loadMore();
      if (this.offset() + 200 < this.store.members().length)
        this.offset.update((offset) => offset + 200);
    } finally {
      this.paging.set(false);
    }
    this.heading().nativeElement.focus();
  }
}
