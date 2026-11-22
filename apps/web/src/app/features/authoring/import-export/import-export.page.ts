import { NgTemplateOutlet } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { HlmIcon } from '@evidgate/ui/icon';
import { HlmTabsImports } from '@evidgate/ui/tabs';
import { SessionStore } from '../../../core/session/session.store';
import { PageHeaderComponent } from '../../../shared/ui/page-header/page-header.component';
import { SectionHeaderComponent } from '../../../shared/ui/page-header/section-header.component';
import { InlineFeedbackComponent } from '../../../shared/ui/states/inline-feedback.component';
import { ImportExportDataService } from './import-export-data.service';
import { ImportPanelComponent } from './import-panel.component';
import { ExportPanelComponent } from './export-panel.component';
import { ImportStore } from './import.store';
import { ExportStore } from './export.store';
import { AUTHORING_READ, IMPEX_COPY } from './import-export.copy';

@Component({
  selector: 'app-authoring-import-export-page',
  providers: [ImportExportDataService, ImportStore, ExportStore],
  imports: [
    RouterLink,
    NgTemplateOutlet,
    HlmIcon,
    HlmButton,
    HlmTabsImports,
    PageHeaderComponent,
    SectionHeaderComponent,
    InlineFeedbackComponent,
    ImportPanelComponent,
    ExportPanelComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <header class="flex min-w-0 flex-wrap items-start justify-between gap-(--eg-space-5)">
      <div class="flex min-w-0 flex-1 items-start gap-(--eg-space-4)">
        <span
          class="grid size-(--eg-control-touch) shrink-0 place-items-center rounded-(--eg-radius-md) bg-(--eg-accent-soft) text-(--eg-accent)"
        >
          <hlm-icon [name]="'lucideCopy'" class="size-(--eg-space-6)" />
        </span>
        @if (ownsPageHeading) {
          <app-page-header class="min-w-0" [title]="copy.title" [description]="copy.description" />
        } @else {
          <app-section-header
            class="min-w-0"
            [title]="copy.title"
            [description]="copy.description"
          />
        }
      </div>
      @if (session.currentProject(); as project) {
        <dl
          class="m-0 grid w-full min-w-0 gap-(--eg-space-1) text-sm md:w-auto md:max-w-1/3"
          [attr.aria-label]="copy.context"
        >
          <div class="flex flex-wrap items-baseline gap-x-(--eg-space-3)">
            <dt class="text-xs text-muted-foreground">{{ copy.project }}</dt>
            <dd class="m-0 font-medium wrap-anywhere">
              <bdi>{{ project.name }}</bdi>
            </dd>
          </div>
          <div class="flex flex-wrap items-baseline gap-x-(--eg-space-3)">
            <dt class="text-xs text-muted-foreground">{{ copy.prefix }}</dt>
            <dd class="m-0 font-mono wrap-anywhere" data-import-prefix>
              <bdi>{{ project.case_prefix }}</bdi>
            </dd>
          </div>
        </dl>
      }
    </header>
    <ng-template #backLink>
      <a
        hlmBtn
        [variant]="'link'"
        [routerLink]="['../']"
        class="min-h-(--eg-control-touch) w-fit whitespace-normal"
        >{{ copy.back }}</a
      >
    </ng-template>
    @if (session.has(readPermission)) {
      <div
        hlmTabs
        [tab]="tab()"
        (tabActivated)="tab.set($event)"
        class="min-w-0 gap-(--eg-space-5)"
      >
        <div class="flex min-w-0 flex-wrap items-center justify-between gap-(--eg-space-3)">
          <div
            hlmTabsList
            [variant]="'line'"
            [attr.aria-label]="copy.title"
            class="h-auto w-fit max-w-full justify-start"
          >
            <button
              type="button"
              [hlmTabsTrigger]="'import'"
              class="min-h-(--eg-control-touch) whitespace-nowrap"
              data-impex-import-tab
            >
              {{ copy.importTab }}
            </button>
            <button
              type="button"
              [hlmTabsTrigger]="'export'"
              class="min-h-(--eg-control-touch) whitespace-nowrap"
              data-impex-export-tab
            >
              {{ copy.exportTab }}
            </button>
          </div>
          <ng-container [ngTemplateOutlet]="backLink" />
        </div>
        <div
          [hlmTabsContent]="'import'"
          [attr.tabindex]="tab() === 'import' ? 0 : -1"
          class="min-w-0 focus-visible:outline-2 focus-visible:outline-ring"
        >
          <app-authoring-import-panel />
        </div>
        <div
          [hlmTabsContent]="'export'"
          [attr.tabindex]="tab() === 'export' ? 0 : -1"
          class="min-w-0 focus-visible:outline-2 focus-visible:outline-ring"
        >
          <app-authoring-export-panel />
        </div>
      </div>
    } @else {
      <ng-container [ngTemplateOutlet]="backLink" />
      <app-inline-feedback [title]="copy.denied" [tone]="'warning'" />
    }
  `,
  host: { class: 'grid min-w-0 gap-(--eg-space-5)' },
})
export class ImportExportPage {
  protected readonly session = inject(SessionStore);
  protected readonly ownsPageHeading =
    inject(ActivatedRoute).snapshot.data['pageHeadingLevel'] === 1;
  protected readonly readPermission = AUTHORING_READ;
  protected readonly copy = IMPEX_COPY;
  protected readonly tab = signal('import');
}
