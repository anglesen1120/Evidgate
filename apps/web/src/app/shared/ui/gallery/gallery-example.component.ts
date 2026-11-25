import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  linkedSignal,
  signal,
} from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmCheckbox } from '@evidgate/ui/checkbox';
import { HlmDialogImports } from '@evidgate/ui/dialog';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmInput } from '@evidgate/ui/input';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';
import { HlmSkeleton } from '@evidgate/ui/skeleton';
import { HlmTabsImports } from '@evidgate/ui/tabs';
import { HlmTooltip } from '@evidgate/ui/tooltip';
import { toast } from '@spartan-ng/brain/sonner';
import { ReviewStatusChipComponent, type ReviewStatus } from '../chips';
import { DashboardKpiComponent, type DashboardMetric } from '../dashboard/dashboard-kpi.component';
import { InlineFeedbackComponent } from '../states';
import { EvidenceBadgeComponent, OutcomePillComponent } from '../verdict';
import { GALLERY_COPY } from './gallery-example-copy';
import { GalleryEvidenceTableComponent } from './gallery-evidence-table.component';
import type { GallerySpecimen, GalleryState } from './gallery-sections';

const samples = [
  { id: 'cand-73', title: GALLERY_COPY.queueTitle, status: 'waiting' },
  { id: 'cand-77', title: GALLERY_COPY.otherTitle, status: 'waiting' },
] as const;
const laterSnapshot = [{ ...samples[0], status: 'over-sla' }, samples[1]] as const;
const destinations = [
  { href: '#evidence', text: GALLERY_COPY.queue },
  { href: '#gallery-decisions', text: GALLERY_COPY.release },
] as const;
const queue = { byOldest: [samples[1], samples[0]], byNewest: samples, overSla: [] } as const;
const laterQueue = {
  byOldest: [laterSnapshot[1], laterSnapshot[0]],
  byNewest: laterSnapshot,
  overSla: [laterSnapshot[0]],
} as const;
const record = {
  project: 'storefront-web',
  reviewer: 'Minh Trần',
  initials: 'MT',
  candidate: samples[0].id,
  nextCandidate: samples[1].id,
  revision: 'ev-3',
  incident: 'inc-92',
  runId: 'run-1842',
  caseId: 'SHOP-TC-0043',
  missingShard: 'shard-3',
  before: "getByTestId('delivery-address-required')",
  after: "getByRole('textbox', { name: 'Required delivery address' })",
  failureAt: '09:43:10 UTC',
  overdueAt: '2026-10-13 16:00 UTC',
} as const;
// This is a separately dated decision preview, never attached to the default queue snapshot.
const approval = {
  approval: { reviewer: 'Minh Trần', at: '2026-10-12T10:00:00Z', candidateHash: 'ch-8af2…3e20' },
} satisfies ReviewStatus;

@Component({
  selector: 'app-gallery-example',
  imports: [
    HlmButton,
    DashboardKpiComponent,
    HlmCardImports,
    HlmCheckbox,
    HlmDialogImports,
    HlmIcon,
    HlmInput,
    HlmLabel,
    HlmSelectImports,
    HlmSkeleton,
    GalleryEvidenceTableComponent,
    HlmTabsImports,
    HlmTooltip,
    ReviewStatusChipComponent,
    InlineFeedbackComponent,
    EvidenceBadgeComponent,
    OutcomePillComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './gallery-example.component.html',
  styleUrl: './gallery-example.component.css',
  host: {
    class: 'grid min-w-0 gap-(--eg-space-3) text-sm wrap-anywhere',
    '[attr.data-state]': 'state()',
    '[attr.aria-busy]': 'state() === "loading" ? true : null',
  },
})
export class GalleryExampleComponent {
  readonly kind = input.required<GallerySpecimen['id']>();
  readonly state = input.required<GalleryState>();
  protected readonly copy = GALLERY_COPY;
  protected readonly approval = approval;
  protected readonly record = record;
  protected readonly id = computed(() => `gallery-${this.kind()}-${this.state()}`);
  protected readonly missing = computed(
    () => this.state() === 'loading' || this.state() === 'empty' || this.state() === 'error',
  );
  protected readonly metrics = computed<readonly DashboardMetric[]>(() => [
    {
      id: 'shadow-pass-rate',
      label: this.copy.kpiLabel,
      value: this.missing() ? null : '100%',
      detail: this.missing()
        ? this.state() === 'empty'
          ? this.copy.noSamples
          : this.caption()
        : this.copy.kpiCaption,
    },
  ]);
  protected readonly locked = computed(
    () => this.state() === 'disabled' || this.state() === 'loading',
  );
  protected readonly active = computed(() => this.state() === 'active');
  protected readonly checked = linkedSignal(() => this.active());
  protected readonly navOpen = signal(true);
  protected readonly search = signal('');
  protected readonly destinations = computed(() => {
    const term = this.search().trim().toLocaleLowerCase();
    return term
      ? destinations.filter(({ text }) => text.toLocaleLowerCase().includes(term))
      : destinations;
  });
  protected readonly newest = signal(false);
  protected readonly lastPage = linkedSignal(() => this.active());
  protected readonly draft = signal<string | null>(GALLERY_COPY.originalMessage);
  protected readonly saved = signal<string | null>(null);
  protected readonly assignee = signal<unknown>('none');
  protected readonly records = computed(() => {
    if (this.missing()) return [];
    const snapshot = this.active() ? laterQueue : queue;
    if (this.checked()) return snapshot.overSla;
    return this.newest() ? snapshot.byNewest : snapshot.byOldest;
  });
  protected readonly caption = computed(() => {
    switch (this.state()) {
      case 'disabled':
        return this.copy.readOnly;
      case 'loading':
        return this.copy.loading;
      case 'empty':
        return this.copy.empty;
      case 'error':
        return this.copy.error;
      case 'success':
        return this.copy.saved;
      default:
        return '';
    }
  });

  protected showNotification(): void {
    toast.success(this.copy.saved);
  }

  protected saveNote(): void {
    const draft = this.draft();
    if (this.locked() || !draft?.trim()) return;
    this.saved.set(draft);
    this.showNotification();
  }
}
