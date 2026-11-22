import {
  afterNextRender,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  Injector,
  linkedSignal,
  signal,
  viewChild,
  viewChildren,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmDropdownMenuImports } from '@evidgate/ui/dropdown-menu';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmTableImports } from '@evidgate/ui/table';
import { StatusPillComponent } from '../chips';
import { NarrowMutationPolicy } from '../narrow/narrow-mutation-policy';
import { LiveStatusComponent } from '../states/live-status.component';
import { SectionHeaderComponent } from '../page-header/section-header.component';
import { GALLERY_COPY } from './gallery-example-copy';
import { GalleryMemberDialogComponent } from './gallery-member-dialog.component';
import { MEMBER_COPY, SAMPLE_MEMBERS, type GalleryMember } from './gallery-member-samples';

type Filter = 'all' | GalleryMember['status'];
const PAGE_SIZE = 2;

@Component({
  selector: 'app-gallery-member-directory',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmButton,
    HlmCard,
    HlmDropdownMenuImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    HlmTableImports,
    StatusPillComponent,
    SectionHeaderComponent,
    GalleryMemberDialogComponent,
    LiveStatusComponent,
  ],
  host: { class: 'block min-w-0' },
  templateUrl: './gallery-member-directory.component.html',
  styleUrl: './gallery-member-directory.component.css',
})
export class GalleryMemberDirectoryComponent {
  private readonly injector = inject(Injector);
  protected readonly policy = inject(NarrowMutationPolicy);
  private readonly listHeading = viewChild.required<ElementRef<HTMLHeadingElement>>('listHeading');
  private readonly viewButtons = viewChildren<ElementRef<HTMLButtonElement>>('view');
  protected readonly copy = MEMBER_COPY;
  protected readonly previous = GALLERY_COPY.previous;
  protected readonly next = GALLERY_COPY.next;
  protected readonly filters: readonly Filter[] = ['all', 'active', 'pending'];
  protected readonly members = signal(SAMPLE_MEMBERS);
  protected readonly announcement = signal('');
  protected readonly filter = signal<Filter>('all');
  protected readonly search = signal('');
  protected readonly role = signal<'all' | 'viewer' | 'reviewer'>('all');
  protected readonly counts = computed(() => {
    const rows = this.members();
    const active = rows.filter((row) => row.status === 'active').length;
    return { all: rows.length, active, pending: rows.length - active };
  });
  protected readonly pendingMember = computed(() =>
    this.members().find((row) => row.status === 'pending'),
  );
  protected readonly subtitle = computed(
    () =>
      $localize`:@@shared.gallery.members.subtitle:Members: ${this.counts().all}:members: · Pending invitations: ${this.counts().pending}:pending:`,
  );
  protected readonly filtered = computed(() => {
    const filter = this.filter();
    const query = this.search().trim().toLocaleLowerCase();
    const role = this.role();
    return this.members().filter(
      (row) =>
        (filter === 'all' || row.status === filter) &&
        (role !== 'reviewer' || row.reviewer) &&
        (!query ||
          row.email.toLowerCase().includes(query) ||
          row.name.toLocaleLowerCase().includes(query)),
    );
  });
  protected readonly page = linkedSignal({
    source: () => [this.filter(), this.role(), this.search()],
    computation: () => 0,
  });
  protected readonly start = computed(() => this.page() * PAGE_SIZE);
  protected readonly visible = computed(() =>
    this.filtered().slice(this.start(), this.start() + PAGE_SIZE),
  );
  protected readonly atEnd = computed(
    () => this.start() + this.visible().length >= this.filtered().length,
  );
  protected readonly range = computed(() => {
    const first = this.visible().length ? this.start() + 1 : 0;
    const last = this.start() + this.visible().length;
    return $localize`:@@shared.gallery.members.range:Showing ${first}:first:–${last}:last: of ${this.filtered().length}:total:`;
  });

  protected chooseRole(value: unknown): void {
    if (value === 'all' || value === 'viewer' || value === 'reviewer') this.role.set(value);
  }

  protected viewLabel(email: string): string {
    return $localize`:@@shared.gallery.members.view-label:View ${email}:email:`;
  }

  protected editLabel(email: string): string {
    return $localize`:@@shared.gallery.members.edit-label:Edit roles for ${email}:email:`;
  }

  protected moreLabel(email: string): string {
    return $localize`:@@shared.gallery.members.more-label:More actions for ${email}:email:`;
  }

  protected applyRoles(member: GalleryMember): void {
    const previousIndex = this.filtered().findIndex((row) => row.email === member.email);
    this.members.update((rows) =>
      rows.map((current) => (current.email === member.email ? member : current)),
    );
    const lastPage = Math.max(0, Math.ceil(this.filtered().length / PAGE_SIZE) - 1);
    if (this.page() > lastPage) this.page.set(lastPage);
    // When the role filter removes the opener, keep the user's position in the directory.
    const remaining = this.filtered();
    if (previousIndex !== -1 && !remaining.some((row) => row.email === member.email)) {
      const neighbour = remaining[Math.min(previousIndex, remaining.length - 1)]?.email;
      afterNextRender(
        () => {
          if (neighbour === undefined) this.listHeading().nativeElement.focus();
          else
            this.viewButtons()
              .find(({ nativeElement }) => nativeElement.value === neighbour)
              ?.nativeElement.focus();
        },
        { injector: this.injector },
      );
    }
    this.announcement.set(
      $localize`:@@shared.gallery.members.applied:Updated sample roles for ${member.email}:email:. No account was changed.`,
    );
  }
}
