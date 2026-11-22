import { ChangeDetectionStrategy, Component, inject, signal, viewChild } from '@angular/core';
import { ActivatedRoute, RouterLink } from '@angular/router';
import { HlmButton } from '@evidgate/ui/button';
import { ShellIconComponent } from '../../../shell/layout/shell-icon.component';
import { HlmTabsImports } from '@evidgate/ui/tabs';
import { SessionStore } from '../../../core/session/session.store';
import { DesktopMutationDirective } from '../../../shared/ui/narrow/desktop-mutation.directive';
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
    ShellIconComponent,
    DesktopMutationDirective,
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
          aria-hidden="true"
          class="grid size-(--eg-space-9) shrink-0 place-items-center rounded-(--eg-radius-md) border border-border bg-(--eg-accent-soft) text-primary md:size-(--eg-space-10)"
        >
          <app-shell-icon [name]="'folder'" class="size-(--eg-space-6)" />
        </span>
        <div class="grid min-w-0 gap-(--eg-space-1)">
          @if (ownsPageHeading) {
            <app-page-header
              class="min-w-0"
              [title]="copy.title"
              [description]="copy.description"
            />
          } @else {
            <app-section-header
              class="min-w-0"
              [title]="copy.title"
              [description]="copy.description"
            />
          }
          @if (session.currentProject(); as project) {
            <dl
              class="m-0 flex min-w-0 flex-wrap items-baseline gap-x-(--eg-space-2) text-xs text-muted-foreground"
              [attr.aria-label]="copy.context"
            >
              <div class="flex flex-wrap items-baseline gap-x-(--eg-space-3)">
                <dt class="sr-only">{{ copy.project }}</dt>
                <dd class="m-0 font-medium wrap-anywhere">
                  <bdi>{{ project.name }}</bdi>
                </dd>
              </div>
              <div class="flex flex-wrap items-baseline gap-x-(--eg-space-3)">
                <dt>
                  <span aria-hidden="true">·</span><span class="sr-only">{{ copy.prefix }}</span>
                </dt>
                <dd class="m-0 font-mono wrap-anywhere" data-import-prefix>
                  <bdi>{{ project.case_prefix }}</bdi>
                </dd>
              </div>
            </dl>
          }
        </div>
      </div>
      <div class="flex min-w-0 flex-wrap items-center gap-(--eg-space-2)">
        <a
          hlmBtn
          [variant]="'outline'"
          [routerLink]="['../']"
          class="min-h-(--eg-control-touch) w-fit whitespace-normal"
          >{{ copy.back }}</a
        >
        @if (tab() === 'import' && session.has(readPermission) && imports.canWrite()) {
          <button
            *appDesktopMutation
            hlmBtn
            type="button"
            data-import-mutation
            data-choose-import-file
            [variant]="imports.work().file ? 'outline' : 'default'"
            [attr.aria-controls]="'authoring-import-file'"
            class="min-h-(--eg-control-touch) whitespace-normal"
            [disabled]="imports.work().pending"
            (focusin)="panel()?.rememberFocus($event)"
            (click)="panel()?.chooseFile()"
            i18n="@@authoring.import-export.choose-file"
          >
            Choose file
          </button>
        }
      </div>
    </header>
    @if (session.has(readPermission)) {
      <div
        hlmTabs
        [tab]="tab()"
        (tabActivated)="tab.set($event)"
        class="min-w-0 gap-(--eg-space-5)"
      >
        <div
          hlmTabsList
          [variant]="'line'"
          [attr.aria-label]="copy.title"
          class="h-auto w-fit min-w-0 max-w-full flex-nowrap justify-start overflow-x-auto overscroll-x-contain p-(--eg-space-2)"
        >
          <button
            type="button"
            [hlmTabsTrigger]="'import'"
            class="min-h-(--eg-control-touch) flex-none whitespace-nowrap"
            data-impex-import-tab
          >
            {{ copy.importTab }}
          </button>
          <button
            type="button"
            [hlmTabsTrigger]="'export'"
            class="min-h-(--eg-control-touch) flex-none whitespace-nowrap"
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
  protected readonly imports = inject(ImportStore);
  protected readonly ownsPageHeading =
    inject(ActivatedRoute).snapshot.data['pageHeadingLevel'] === 1;
  protected readonly panel = viewChild(ImportPanelComponent);
  protected readonly readPermission = AUTHORING_READ;
  protected readonly copy = IMPEX_COPY;
  protected readonly tab = signal('import');
}
