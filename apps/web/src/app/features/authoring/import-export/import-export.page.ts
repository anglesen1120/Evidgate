import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
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
    @if (ownsPageHeading) {
      <app-page-header [title]="copy.title" [description]="copy.description" />
    } @else {
      <app-section-header [title]="copy.title" [description]="copy.description" />
    }
    <a
      hlmBtn
      [variant]="'link'"
      [routerLink]="['../']"
      class="min-h-(--eg-control-touch) w-fit whitespace-normal"
      >{{ copy.back }}</a
    >
    @if (session.currentProject(); as project) {
      <dl
        class="grid min-w-0 gap-(--eg-space-3) rounded-lg border bg-card p-(--eg-space-4) md:grid-cols-2"
        [attr.aria-label]="copy.context"
      >
        <div class="min-w-0">
          <dt class="text-sm text-muted-foreground">{{ copy.project }}</dt>
          <dd class="m-0 wrap-anywhere">{{ project.name }}</dd>
        </div>
        <div class="min-w-0">
          <dt class="text-sm text-muted-foreground">{{ copy.prefix }}</dt>
          <dd class="m-0 font-mono wrap-anywhere" data-import-prefix>{{ project.case_prefix }}</dd>
        </div>
      </dl>
    }
    @if (session.has(readPermission)) {
      <div
        hlmTabs
        [tab]="tab()"
        (tabActivated)="tab.set($event)"
        class="min-w-0 gap-(--eg-space-6)"
      >
        <div
          hlmTabsList
          [variant]="'line'"
          [attr.aria-label]="copy.title"
          class="h-auto w-full justify-start"
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
