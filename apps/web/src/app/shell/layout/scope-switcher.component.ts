import { DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
} from '@angular/core';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';
import { HlmLabel } from '@evidgate/ui/label';
import { HlmSelectImports } from '@evidgate/ui/select';

type Scope = 'org' | 'project';

@Component({
  selector: 'app-scope-switcher',
  imports: [HlmLabel, HlmSelectImports],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './scope-switcher.component.html',
  host: {
    class: 'group/scope grid min-w-0 gap-(--eg-space-3)',
    '[class.compact]': 'compact()',
    '[attr.data-compact]': 'compact()',
  },
})
export class ScopeSwitcherComponent {
  private static nextId = 0;
  readonly orgId = `scope-org-${ScopeSwitcherComponent.nextId++}`;
  readonly projectId = `${this.orgId}-project`;
  readonly compact = input(false);
  readonly organizations = input<readonly OrganizationSummaryDto[]>([]);
  readonly projects = input<readonly ProjectDto[]>([]);
  readonly currentOrg = input<OrganizationSummaryDto | null>(null);
  readonly currentProject = input<ProjectDto | null>(null);
  readonly orgSelected = output<string>();
  readonly projectSelected = output<string>();
  /**
   * The Escape an open scope popup consumed. A containing drawer reads it to stay open, because the
   * popup flushes its closed state before the drawer's own keydown handler runs — a rendered
   * `aria-expanded` probe misses there, while the popup state read here cannot.
   */
  readonly popupEscape = output<KeyboardEvent>();
  readonly chooseOrganization = $localize`:@@shell.switcher.choose-organization:Choose an organization`;
  /** Whether a scope has nothing to offer: its select is disabled and cannot own a popup. */
  readonly orgDisabled = computed(() => this.organizations().length === 0);
  readonly projectDisabled = computed(
    () => this.projects().length === 0 || this.currentOrg() === null,
  );
  readonly chooseProject = $localize`:@@shell.switcher.choose-project:Choose a project`;
  readonly noProjects = $localize`:@@shell.switcher.no-projects:No projects`;
  private readonly document = inject(DOCUMENT);
  private readonly openPopups = new Set<Scope>();
  /**
   * Capture on the window, so it reads the popup state before any handler that dismisses it for the
   * same Escape, including the one that renders `aria-expanded` closed.
   */
  private readonly recordEscape = (event: KeyboardEvent): void => {
    if (event.key === 'Escape' && this.openPopups.size > 0) this.popupEscape.emit(event);
  };

  constructor() {
    const view = this.document.defaultView;
    view?.addEventListener('keydown', this.recordEscape, true);
    inject(DestroyRef).onDestroy(() => {
      // A popup torn down with its select reports no close of its own, so no entry may outlive the
      // switcher and claim an Escape that is no longer the popup's.
      this.openPopups.clear();
      view?.removeEventListener('keydown', this.recordEscape, true);
    });
    // A select that becomes disabled cannot open a popup any more, and the one it had open can no
    // longer report its close: keeping the entry would swallow the drawer's next Escape instead.
    effect(() => {
      if (this.orgDisabled()) this.openPopups.delete('org');
      if (this.projectDisabled()) this.openPopups.delete('project');
    });
  }

  /** Tracks the popup state Brain reports, rather than the async rendered attribute. */
  popupState(scope: Scope, state: unknown): void {
    if (state === 'open') this.openPopups.add(scope);
    else this.openPopups.delete(scope);
  }

  // Brain commits only activated options; arrow-key exploration does not navigate.
  select(scope: Scope, value: unknown): void {
    if (typeof value !== 'string' || value === '') return;
    if (scope === 'org') {
      if (value === this.currentOrg()?.id) return;
      this.orgSelected.emit(value);
    } else {
      if (value === this.currentProject()?.id) return;
      this.projectSelected.emit(value);
    }
  }
}
