import {
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
import { FormsModule } from '@angular/forms';
import { ActivatedRoute } from '@angular/router';
import { HlmBadge } from '@evidgate/ui/badge';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardTitle } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { DesktopRequiredNoticeComponent, NarrowMutationPolicy } from '../../../shared/ui/narrow';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import {
  EmptyPatternComponent,
  InlineFeedbackComponent,
  LoadingPatternComponent,
} from '../../../shared/ui/states';
import {
  GalleryDraftDialogComponent,
  type GalleryDraftMode,
} from './gallery-draft-dialog.component';
import { GalleryTemplateCardComponent } from './gallery-template-card.component';
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
    FormsModule,
    HlmBadge,
    HlmButton,
    HlmCard,
    HlmCardTitle,
    HlmIcon,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    PageHeaderImports,
    EmptyPatternComponent,
    InlineFeedbackComponent,
    LoadingPatternComponent,
    DesktopRequiredNoticeComponent,
    GalleryTemplateCardComponent,
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
  private readonly previewPanel = viewChild<ElementRef<HTMLElement>>('preview');
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
  protected readonly locked = computed(
    () => !this.picked() || ['403', '412', '429', 'success', 'busy'].includes(this.state()),
  );
  protected readonly dialog = linkedSignal<GalleryDraftMode | null>(() => {
    const state = this.state();
    if (this.policy.narrow()) return null;
    return state === 'busy' || state === 'invalid' || state === 'createerror' ? state : null;
  });
  protected readonly sources = [
    { id: 'all', label: galleryCopy.all },
    { id: 'built', label: galleryCopy.built },
    { id: 'team', label: galleryCopy.team },
  ] as const;
  protected readonly sourceLabel = (id: unknown) =>
    this.sources.find((source) => source.id === id)?.label ?? '';

  protected previewTemplate(id: GalleryTemplateViewModel['id']): void {
    this.store.choose(id);
    this.previewPanel()?.nativeElement.focus();
  }

  protected openDraft(): void {
    if (!this.locked() && !this.policy.narrow()) this.dialog.set('preview');
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
