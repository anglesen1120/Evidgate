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
  /**
   * A paging attempt the server never answered (a contract violation: a repeated cursor, the page
   * cap, a malformed envelope). `MembersStore.loadMore` re-throws those instead of recording them,
   * so the page owns this flag rather than inventing an `ApiError` the server never sent.
   */
  private readonly pagingFailure = signal(false);
  /**
   * Whether the store's `loadError` is stale. A refused page sets it and keeps `phase` `ready`
   * (`members.store.ts` `loadMore`), and a later page that arrives does not clear it, so the page
   * hides it once a page loaded — the store is not edited for this.
   */
  private readonly storedErrorCleared = signal(false);
  /**
   * The paging alert above the pagination nav: the server's own refusal with its code, or the
   * generic message for a contract violation. `null` while the request-state owns the read state
   * (`phase` not `ready`), because there the shared error pattern already speaks for the page.
   */
  protected readonly pagingAlert = computed<{ message: string; code: string | null } | null>(() => {
    if (this.store.phase() !== 'ready') return null;
    if (this.pagingFailure()) return { message: this.copy.loadMoreFailed, code: null };
    if (this.storedErrorCleared()) return null;
    const failure = this.store.loadError();
    return failure === null ? null : { message: failure.message, code: failure.code };
  });
  private readonly heading = viewChild.required<ElementRef<HTMLHeadingElement>>('heading');
  private readonly inviteDialog = viewChild(InviteDialogComponent);
  private readonly removeDialog = viewChild(RemoveDialogComponent);

  constructor() {
    effect(() => {
      const org = this.session.currentOrg();
      this.offset.set(0);
      this.saved.set(false);
      untracked(() => {
        this.clearPagingAlert();
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
      this.clearPagingAlert();
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
    this.clearPagingAlert();
    if (removed) this.heading().nativeElement.focus();
  }
  protected previous(): void {
    this.offset.update((offset) => Math.max(0, offset - 200));
    this.heading().nativeElement.focus();
  }
  /**
   * Paging. Next stays the retry control: a refused page leaves the rows and the cursor alone, so
   * the same button asks again, and this method never rejects — the click handler would otherwise
   * be an unhandled rejection. A typed server refusal already lives in `loadError`; anything the
   * store re-throws (a contract violation) becomes the generic alert, never a message built from
   * the thrown value.
   */
  protected async next(): Promise<void> {
    if (this.paging()) return;
    this.paging.set(true);
    const previousError = this.store.loadError();
    try {
      if (this.offset() + 200 >= this.store.members().length) {
        await this.store.loadMore();
        // The store keeps a refusal in `loadError` and does not clear it when a later page
        // arrives, so the page clears its own copy: an unchanged error means this read answered.
        this.storedErrorCleared.set(this.store.loadError() === previousError);
      } else {
        this.storedErrorCleared.set(true);
      }
      this.pagingFailure.set(false);
      if (this.offset() + 200 < this.store.members().length)
        this.offset.update((offset) => offset + 200);
    } catch {
      this.pagingFailure.set(true);
      this.storedErrorCleared.set(false);
    } finally {
      this.paging.set(false);
    }
    // A page that answered moves the reader to the list heading; a refusal or a violation leaves
    // focus where it was — on Next, the retry control, next to the alert that just appeared —
    // instead of scrolling away from the alert to a heading whose rows did not change.
    if (this.pagingAlert() === null) this.heading().nativeElement.focus();
  }

  /** Forgets both paging alerts, so a new org, reload or mutation does not inherit one. */
  private clearPagingAlert(): void {
    this.pagingFailure.set(false);
    this.storedErrorCleared.set(false);
  }
}
