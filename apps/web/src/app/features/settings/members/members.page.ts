import { DecimalPipe } from '@angular/common';
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
import { ActivatedRoute } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmAlertImports } from '@evidgate/ui/alert';
import { HlmCardImports } from '@evidgate/ui/card';
import type { MembershipView } from '@evidgate/sdk';
import { SessionStore } from '../../../core/session/session.store';
import { RequestStateComponent } from '../../../shared/ui/states/request-state.component';
import { NarrowMutationPolicy } from '../../../shared/ui/narrow/narrow-mutation-policy';
import { DataTablePagerComponent } from '../../../shared/ui/data-table/data-table-pager.component';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import {
  SettingsPageCountsComponent,
  type SettingsPageCount,
} from '../settings-page-counts.component';
import { EditRolesDialogComponent } from './edit-roles-dialog.component';
import { InviteDialogComponent } from './invite-dialog.component';
import { MemberListComponent, MEMBERS_COPY } from './member-list.component';
import { MembersStore } from './members.store';
import { RemoveDialogComponent } from './remove-dialog.component';
import { RoleMatrixComponent } from './role-matrix.component';
import { ResendInviteDialogComponent } from './resend-invite-dialog.component';
import { CancelInviteDialogComponent } from './cancel-invite-dialog.component';

/** Org-scoped in both the Settings tab and the Members sidebar destination. */
@Component({
  selector: 'app-members-page',
  imports: [
    DecimalPipe,
    HlmButton,
    HlmAlertImports,
    HlmCardImports,
    ShellIconComponent,
    SettingsPageCountsComponent,
    RequestStateComponent,
    MemberListComponent,
    DataTablePagerComponent,
    RoleMatrixComponent,
    InviteDialogComponent,
    RemoveDialogComponent,
    EditRolesDialogComponent,
    ResendInviteDialogComponent,
    CancelInviteDialogComponent,
  ],
  providers: [MembersStore],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './members.page.html',
  host: {
    class: 'block min-w-0 font-sans text-sm text-foreground',
    '[attr.data-members-phase]': 'store.phase()',
  },
})
export class MembersPage {
  protected readonly store = inject(MembersStore);

  protected readonly session = inject(SessionStore);

  private readonly viewport = inject(NarrowMutationPolicy);

  protected readonly ownsPageHeading =
    inject(ActivatedRoute, { optional: true })?.snapshot.data['pageHeadingLevel'] === 1;

  protected readonly copy = MEMBERS_COPY;
  protected readonly pageHeadingSize = 'page' as const;
  protected readonly canWrite = computed(() => this.session.has('membership:write'));
  protected readonly status = computed<'loading' | 'ready' | 'error'>(() => {
    const phase = this.store.phase();
    return phase === 'idle' ? 'loading' : phase;
  });
  protected readonly visibleMembers = this.store.members;
  protected readonly counts = computed(() => {
    let active = 0;
    let pending = 0;
    let admins = 0;
    for (const member of this.visibleMembers()) {
      if (member.status === 'active') {
        active++;
        if (member.roles.some((role) => role.is_builtin && role.code === 'tenant_admin')) admins++;
      } else if (member.status === 'pending') {
        pending++;
      }
    }
    return { active, pending, admins };
  });
  protected readonly pageCounts = computed<readonly SettingsPageCount[]>(() => {
    const { active, pending, admins } = this.counts();
    return [
      {
        label: this.copy.activeMembers,
        icon: 'people',
        value: active,
        hint: $localize`:@@settings.members.active-hint:On this page · organization directory`,
      },
      {
        label: this.copy.pendingInvitations,
        icon: 'clock',
        value: pending,
        hint: $localize`:@@settings.members.pending-hint:On this page · awaiting acceptance`,
      },
      {
        label: this.copy.organizationAdmins,
        icon: 'shield',
        value: admins,
        hint: $localize`:@@settings.members.admin-hint:On this page · last admin protected`,
      },
    ];
  });
  protected readonly visibleMatrix = computed(() => {
    const ids = new Set(this.visibleMembers().map((member) => member.id));
    const matrix = this.store.matrix();
    return {
      columns: matrix.columns,
      rows: matrix.rows.filter((row) => ids.has(row.membership.id)),
    };
  });
  protected readonly paging = computed(() => this.store.busy());
  protected readonly saved = signal(false);
  protected readonly retryReady = signal(true);
  /**
   * A paging attempt the server never answered (a contract violation: a repeated cursor, the page
   * cap, a malformed envelope). `MembersStore.loadMore` re-throws those instead of recording them,
   * so the page owns this flag rather than inventing an `ApiError` the server never sent.
   */
  private readonly pagingFailure = signal(false);
  /**
   * The paging alert above the pagination nav: the server's own refusal with its code, or the
   * generic message for a contract violation. `null` while the request-state owns the read state
   * (`phase` not `ready`), because there the shared error pattern already speaks for the page.
   */
  protected readonly pagingAlert = computed<{ message: string; code: string | null } | null>(() => {
    if (this.store.phase() !== 'ready') return null;
    if (this.pagingFailure())
      return {
        message: $localize`:@@settings.members.page-load-failed:The members page could not be loaded. The members already shown are unchanged.`,
        code: null,
      };
    const failure = this.store.loadError();
    return failure === null ? null : { message: failure.message, code: failure.code };
  });
  private readonly heading = viewChild.required<ElementRef<HTMLHeadingElement>>('heading');
  private readonly inviteDialog = viewChild(InviteDialogComponent);
  private readonly removeDialog = viewChild(RemoveDialogComponent);
  private readonly editDialog = viewChild(EditRolesDialogComponent);
  private readonly resendDialog = viewChild(ResendInviteDialogComponent);
  private readonly cancelInviteDialog = viewChild(CancelInviteDialogComponent);

  constructor() {
    effect(() => {
      const org = this.session.currentOrg();
      this.saved.set(false);
      untracked(() => {
        this.clearPagingAlert();
        this.closeDialogs();
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
      this.clearPagingAlert();
      void this.store.load(org.id);
    }
  }
  protected openInvite(event: Event): void {
    if (
      this.viewport.allowsMutation() &&
      this.canWrite() &&
      event.currentTarget instanceof HTMLElement
    ) {
      this.closeDialogs();
      this.inviteDialog()?.open(event.currentTarget);
    }
  }
  protected openRemove(event: { member: MembershipView; opener: HTMLElement }): void {
    if (this.viewport.allowsMutation() && this.canWrite()) {
      this.closeDialogs();
      this.removeDialog()?.open(event.member, event.opener);
    }
  }
  protected openEditRoles(event: { member: MembershipView; opener: HTMLElement }): void {
    if (this.viewport.allowsMutation() && this.canWrite()) {
      this.closeDialogs();
      this.editDialog()?.open(event.member, event.opener);
    }
  }
  protected openResend(event: { member: MembershipView; opener: HTMLElement }): void {
    if (this.viewport.allowsMutation() && this.canWrite()) {
      this.closeDialogs();
      this.resendDialog()?.open(event.member, event.opener);
    }
  }
  protected openCancelInvite(event: { member: MembershipView; opener: HTMLElement }): void {
    if (this.viewport.allowsMutation() && this.canWrite()) {
      this.closeDialogs();
      this.cancelInviteDialog()?.open(event.member, event.opener);
    }
  }
  private closeDialogs(): void {
    this.inviteDialog()?.close();
    this.removeDialog()?.close();
    this.editDialog()?.close();
    this.resendDialog()?.close();
    this.cancelInviteDialog()?.close();
  }
  protected completed(removed = false): void {
    this.saved.set(true);
    this.clearPagingAlert();
    if (removed) this.heading().nativeElement.focus();
  }
  protected previous(): Promise<void> {
    return this.navigate(false);
  }

  protected next(): Promise<void> {
    return this.navigate(true);
  }

  protected async changePageSize(size: number): Promise<void> {
    this.clearPagingAlert();
    await this.store.setPageSize(size);
  }

  private async navigate(forward: boolean): Promise<void> {
    if (this.paging() || !this.retryReady()) return;
    try {
      await (forward ? this.store.loadMore() : this.store.previous());
      this.pagingFailure.set(false);
    } catch {
      this.pagingFailure.set(true);
    }
    if (this.pagingAlert() === null) this.heading().nativeElement.focus();
  }

  /** Forgets both paging alerts, so a new org, reload or mutation does not inherit one. */
  private clearPagingAlert(): void {
    this.pagingFailure.set(false);
  }
}
