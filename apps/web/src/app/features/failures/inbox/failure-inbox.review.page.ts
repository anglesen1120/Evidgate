import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard } from '@evidgate/ui/card';
import { HlmIcon } from '@evidgate/ui/icon';
import { AbsoluteTimeComponent } from '../../../shared/time/absolute-time.component';
import { DesktopRequiredNoticeComponent } from '../../../shared/ui/narrow';
import { PageHeaderImports } from '../../../shared/ui/page-header';
import { INBOX_COPY as c } from './failure-inbox.copy';
import { AS_OF, PROJECT_CONTEXT } from './failure-inbox.fixture';
import { FailureInboxStore } from './failure-inbox.store';
import { InboxPreviewComponent } from './inbox-preview.component';
import { InboxQueueComponent } from './inbox-queue.component';
import { InboxReviewDialogComponent } from './inbox-review-dialog.component';
import { InboxStateComponent } from './inbox-state.component';
import { InboxSummaryComponent } from './inbox-summary.component';

// Review-only until incident triage behaviour lands. Its data service will replace
// these static records; previewing a change here must never alter original evidence.
@Component({
  selector: 'app-failure-inbox-review',
  providers: [FailureInboxStore],
  imports: [
    AbsoluteTimeComponent,
    DesktopRequiredNoticeComponent,
    HlmButton,
    HlmCard,
    HlmIcon,
    InboxPreviewComponent,
    InboxQueueComponent,
    InboxReviewDialogComponent,
    InboxStateComponent,
    InboxSummaryComponent,
    PageHeaderImports,
    RouterLink,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0 text-sm text-foreground wrap-anywhere' },
  templateUrl: './failure-inbox.review.page.html',
})
export class FailureInboxReviewPage {
  protected readonly store = inject(FailureInboxStore);
  protected readonly c = c;
  protected readonly asOf = AS_OF;
  protected readonly projectContext = PROJECT_CONTEXT;
  protected readonly nextIncident = computed(
    () =>
      this.store.matches().find((row) => row.status === 'open')?.id ?? this.store.matches()[0]?.id,
  );
  protected readonly kpis = [
    { label: c.incidents, value: 6, hint: c.labelsCount, icon: 'lucideActivity' },
    { label: c.open, value: 4, hint: c.awaiting, icon: 'lucideClock' },
    { label: c.triaged, value: 2, hint: c.reviewed, icon: 'lucideCheck' },
    { label: c.candidates, value: 1, hint: c.locatorOnly, icon: 'lucideInfo' },
  ] as const;
}
