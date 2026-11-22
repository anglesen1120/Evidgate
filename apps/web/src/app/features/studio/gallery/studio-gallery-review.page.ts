import {
  afterRenderEffect,
  ChangeDetectionStrategy,
  Component,
  computed,
  ElementRef,
  inject,
  input,
  linkedSignal,
  viewChild,
} from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { ActivatedRoute } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { DesktopRequiredNoticeComponent, NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { PageHeaderComponent, PageHeaderImports } from '../../../shared/ui/page-header';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import {
  EmptyPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
} from '../../../shared/ui/states';
import {
  GalleryDraftDialogComponent,
  type GalleryDraftMode,
} from './gallery-draft-dialog.component';
import { GalleryTemplateTableComponent } from './gallery-template-table.component';
import { GalleryTemplateViewComponent } from './gallery-template-view.component';
import { galleryCopy } from './studio-gallery.copy';
import {
  galleryFixture,
  type GalleryTemplateViewModel,
  type StudioGalleryViewModel,
} from './studio-gallery.fixture';
import { GalleryReviewStore } from './studio-gallery.store';

const states = [
  'default',
  'loading',
  'empty',
  'no-match',
  'error',
  '403',
  '404',
  '401',
  '429',
  '412',
  'success',
  'invalid',
  'busy',
  'createerror',
  'record-unavailable',
] as const;
type GalleryState = (typeof states)[number];
function scenario(value: string | null | undefined): GalleryState {
  if (value === 'nomatch') return 'no-match';
  return states.find((state) => state === value) ?? 'default';
}

// Review-only until Studio behaviour lands: its data service will replace the historical fixture.
@Component({
  selector: 'app-studio-gallery-review',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    HlmBadge,
    HlmButton,
    HlmCard,
    ShellIconComponent,
    HlmIcon,
    HlmInput,
    HlmLabel,
    PageHeaderImports,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    DesktopRequiredNoticeComponent,
    GalleryTemplateTableComponent,
    GalleryTemplateViewComponent,
    GalleryDraftDialogComponent,
  ],
  providers: [GalleryReviewStore],
  templateUrl: './studio-gallery-review.page.html',
  styleUrl: './studio-gallery-review.page.css',
})
export class StudioGalleryReviewPage {
  private readonly route = inject(ActivatedRoute);
  protected readonly store = inject(GalleryReviewStore);
  protected readonly policy = inject(NarrowMutationPolicy);
  readonly viewModel = input<StudioGalleryViewModel>(galleryFixture);
  private readonly params = toSignal(this.route.queryParamMap);
  private readonly table = viewChild(GalleryTemplateTableComponent);
  private readonly header = viewChild.required(PageHeaderComponent);
  private readonly createButton = viewChild<ElementRef<HTMLButtonElement>>('createButton');
  private returnTo: GalleryTemplateViewModel['id'] | 'create' | null = null;
  private readonly queryBox = viewChild<ElementRef<HTMLInputElement>>('queryBox');
  protected readonly copy = galleryCopy;
  protected readonly state = linkedSignal(() => scenario(this.params()?.get('state')));
  protected readonly showGallery = computed(
    () => !['loading', 'empty', 'error', '404', '401', 'record-unavailable'].includes(this.state()),
  );
  protected readonly visible = computed(() =>
    this.state() === 'no-match' ? [] : this.store.filter(this.viewModel().templates),
  );
  protected readonly picked = computed(
    () => this.visible().find((t) => t.id === this.store.picked()) ?? this.visible()[0],
  );
  protected readonly viewed = linkedSignal({
    source: () => ({ rows: this.visible(), scope: this.viewModel().scope, params: this.params() }),
    computation: (): GalleryTemplateViewModel | null => null,
  });
  protected readonly locked = computed(
    () => !this.picked() || ['403', '412', '429', 'success', 'busy'].includes(this.state()),
  );
  protected readonly dialog = linkedSignal<
    { state: GalleryState; narrow: boolean; scope: string },
    GalleryDraftMode | null
  >({
    source: () => ({
      state: this.state(),
      narrow: this.policy.narrow(),
      scope: this.viewModel().scope,
    }),
    computation: ({ state, narrow, scope }, previous) => {
      if (previous && previous.source.scope !== scope) return null;
      // Keep an already-open form on resize; the dialog becomes read-only without erasing local edits.
      if (previous?.source.state === state && previous.value) return previous.value;
      if (narrow) return null;
      return state === 'busy' || state === 'invalid' || state === 'createerror' ? state : null;
    },
  });
  protected readonly sources = computed(() => {
    const rows = this.viewModel().templates;
    const built = rows.reduce((count, row) => count + Number(row.source === 'built'), 0);
    return [
      { id: 'all', label: galleryCopy.all, count: rows.length, icon: 'grid' },
      { id: 'built', label: galleryCopy.built, count: built, icon: 'layers' },
      { id: 'team', label: galleryCopy.team, count: rows.length - built, icon: 'users' },
    ] as const;
  });
  protected readonly shown = computed(() =>
    galleryCopy.showing(this.visible().length, this.viewModel().templates.length),
  );

  constructor() {
    afterRenderEffect(() => {
      const viewed = this.viewed();
      if (viewed) this.returnTo = viewed.id;
      else if (this.dialog() && this.showGallery() && this.picked()) this.returnTo ??= 'create';
      else if (this.returnTo) {
        const target = this.returnTo;
        this.returnTo = null;
        if (target === 'create') {
          const button = this.createButton()?.nativeElement;
          if (button && !button.disabled) button.focus();
          else this.header().focusTitle();
        } else if (!this.table()?.focusView(target)) {
          this.header().focusTitle();
        }
      }
    });
  }

  protected previewTemplate(id: GalleryTemplateViewModel['id']): void {
    const row = this.visible().find((template) => template.id === id);
    if (!row || !this.showGallery() || this.dialog()) return;
    this.store.choose(id);
    this.viewed.set(row);
  }

  protected openDraft(): void {
    if (this.locked() || this.policy.narrow()) return;
    this.viewed.set(null);
    this.dialog.set('preview');
  }

  protected closeDraft(): void {
    this.dialog.set(null);
    if (['busy', 'createerror', 'invalid'].includes(this.state())) this.state.set('default');
  }

  protected clear(): void {
    this.store.reset();
    if (this.state() === 'no-match') this.state.set('default');
    this.queryBox()?.nativeElement.focus();
  }
}
