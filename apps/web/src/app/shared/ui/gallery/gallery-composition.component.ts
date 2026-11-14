import { ChangeDetectionStrategy, Component, signal } from '@angular/core';
import { HlmButton } from '@evidgate/ui/button';
import { HlmCard, HlmCardContent, HlmCardHeader } from '@evidgate/ui/card';
import { HlmDialogFooter } from '@evidgate/ui/dialog';

import { AbsoluteTimeComponent } from '../../time/absolute-time.component';
import { SectionHeaderComponent } from '../page-header/section-header.component';
import {
  CopyableIdentifierComponent,
  CopyValueButtonComponent,
  NumberValueComponent,
} from '../values';

/** Render-only specimens for heading roles, value formats and dialog action order. */
@Component({
  selector: 'app-gallery-composition',
  imports: [
    HlmButton,
    HlmCard,
    HlmCardContent,
    HlmCardHeader,
    HlmDialogFooter,
    SectionHeaderComponent,
    AbsoluteTimeComponent,
    CopyableIdentifierComponent,
    CopyValueButtonComponent,
    NumberValueComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'block min-w-0' },
  template: `
    <section
      class="grid min-w-0 gap-4 border-0 border-t border-solid border-border py-4"
      aria-labelledby="gallery-heading-roles"
    >
      <app-section-header
        [title]="sectionTitle"
        [description]="sectionDescription"
        [titleId]="'gallery-heading-roles'"
      />
      <app-section-header [title]="subsectionTitle" [headingLevel]="3" [size]="'subsection'" />
      <section hlmCard class="min-w-0" aria-labelledby="gallery-card-heading">
        <div hlmCardHeader>
          <app-section-header
            [title]="subsectionTitle"
            [headingLevel]="3"
            [size]="'subsection'"
            [titleId]="'gallery-card-heading'"
          />
        </div>
        <div hlmCardContent class="grid min-w-0 gap-3">
          <p class="eg-type-body m-0 wrap-anywhere">{{ sectionDescription }}</p>
          <p class="eg-type-caption m-0 wrap-anywhere">{{ pageDescription }}</p>
          <p class="eg-type-body m-0 wrap-anywhere">{{ longBranch }}</p>
          <button
            hlmBtn
            class="eg-header-action"
            type="button"
            (click)="actionCount.update(increment)"
          >
            {{ longAction }}
          </button>
        </div>
      </section>
    </section>

    <section
      class="grid min-w-0 gap-3 border-0 border-t border-solid border-border py-4"
      aria-labelledby="gallery-value-formats"
    >
      <h2
        id="gallery-value-formats"
        class="eg-type-section-title m-0"
        i18n="@@shared.gallery.values.title"
      >
        Times, numbers and identifiers
      </h2>
      <app-absolute-time [value]="'2026-11-07T15:10:00.123456+07:00'" />
      <app-absolute-time [value]="'2026-02-30T00:00:00Z'" />
      <app-absolute-time [value]="null" />
      <div class="min-w-0 max-w-full" style="max-inline-size: 24rem">
        <app-copyable-identifier [value]="hash" [label]="hashLabel" />
        <app-copyable-identifier [value]="longBranch" [label]="branchLabel" />
      </div>
      <app-copyable-identifier [value]="hash" [label]="hashLabel" [truncate]="false" />
      <app-copy-value-button [value]="hash" [label]="hashLabel" />
      <app-number-value [value]="'1234567890123456.123456'" [unit]="requestUnit" />
      <app-number-value [value]="0" [unit]="requestUnit" />
      <app-number-value [value]="null" />
    </section>

    <section
      class="grid min-w-0 gap-4 border-0 border-t border-solid border-border py-4"
      aria-labelledby="gallery-dialog-footers"
    >
      <h2
        id="gallery-dialog-footers"
        class="eg-type-section-title m-0"
        i18n="@@shared.gallery.dialog.title"
      >
        Dialog action order
      </h2>
      <div hlmDialogFooter>
        <button hlmBtn [variant]="'outline'" type="button" (click)="actionCount.set(0)">
          {{ cancel }}
        </button>
        <button hlmBtn type="button" (click)="actionCount.update(increment)">
          {{ longAction }}
        </button>
      </div>
      <div hlmDialogFooter>
        <button hlmBtn [variant]="'outline'" type="button" (click)="actionCount.set(0)">
          {{ cancel }}
        </button>
        <button
          hlmBtn
          [variant]="'destructive'"
          type="button"
          (click)="actionCount.update(increment)"
        >
          {{ remove }}
        </button>
      </div>
      <div hlmDialogFooter>
        <button hlmBtn [variant]="'outline'" type="button" disabled>{{ cancel }}</button>
        <button hlmBtn type="button" disabled i18n="@@shared.gallery.dialog.submitting">
          Saving project connection…
        </button>
      </div>
      <output i18n="@@shared.gallery.local-actions"
        >Local example actions: {{ actionCount() }}</output
      >
    </section>
  `,
})
export class GalleryCompositionComponent {
  protected readonly pageDescription = $localize`:@@shared.gallery.headings.page-description:Inspect recorded runs and project connection details.`;
  protected readonly sectionTitle = $localize`:@@shared.gallery.headings.section-title:Recorded attempts`;
  protected readonly sectionDescription = $localize`:@@shared.gallery.headings.section-description:Every supplied attempt remains available for inspection.`;
  protected readonly subsectionTitle = $localize`:@@shared.gallery.headings.subsection-title:Connection health`;
  protected readonly longAction = $localize`:@@shared.gallery.actions.connect-long:Connect project and inspect recorded evidence`;
  protected readonly cancel = $localize`:@@shared.gallery.actions.cancel:Cancel`;
  protected readonly remove = $localize`:@@shared.gallery.actions.remove:Remove the example project connection`;
  protected readonly hashLabel = $localize`:@@shared.gallery.values.hash:Policy hash`;
  protected readonly branchLabel = $localize`:@@shared.gallery.values.branch:Branch`;
  protected readonly requestUnit = $localize`:@@shared.gallery.values.requests:requests`;
  protected readonly hash =
    'sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef';
  protected readonly longBranch =
    'feature/international-evidence-review-0123456789abcdef0123456789abcdef0123456789abcdef';
  protected readonly actionCount = signal(0);
  protected readonly increment = (count: number) => count + 1;
}
