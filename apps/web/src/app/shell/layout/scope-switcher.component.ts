import { ChangeDetectionStrategy, Component, input, output, signal } from '@angular/core';
import type { OrganizationSummaryDto, ProjectDto } from '@evidgate/sdk';

/** Which of the two scope controls a value belongs to. */
type Scope = 'org' | 'project';

/**
 * Keys that step a closed native `<select>` through its options. Chromium and Firefox fire
 * `change` on each of these keypresses, so committing on `change` would navigate on every step
 * (WCAG 3.2.2 On Input) — the switcher holds the stepped value until Enter or blur commits it.
 */
const STEP_KEYS: Readonly<Record<string, true>> = {
  ArrowDown: true,
  ArrowUp: true,
  Home: true,
  End: true,
  PageDown: true,
  PageUp: true,
};

/**
 * Render-only scope controls. The router and its guards remain the source of truth.
 *
 * A change commits immediately unless the visitor is stepping through the options with the
 * keyboard; the select's own value is then the pending choice and Enter or leaving the control
 * commits it (see `STEP_KEYS`).
 */
@Component({
  selector: 'app-scope-switcher',
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './scope-switcher.component.html',
  styleUrl: './scope-switcher.component.css',
  host: { '[class.compact]': 'compact()' },
})
export class ScopeSwitcherComponent {
  readonly compact = input(false);
  readonly organizations = input<readonly OrganizationSummaryDto[]>([]);
  readonly projects = input<readonly ProjectDto[]>([]);
  readonly currentOrg = input<OrganizationSummaryDto | null>(null);
  readonly currentProject = input<ProjectDto | null>(null);
  readonly orgSelected = output<string>();
  readonly projectSelected = output<string>();

  /** True while the last input on a select was a stepping key: `change` is held, not emitted. */
  private readonly stepping = signal(false);

  onKeydown(scope: Scope, value: string, event: KeyboardEvent): void {
    if (event.key === 'Enter') {
      this.stepping.set(false);
      this.emit(scope, value);
      return;
    }
    if (STEP_KEYS[event.key] === true) {
      this.stepping.set(true);
      return;
    }
    // A printable key is a typeahead pick, so the change that follows commits at once. Any other
    // key (Tab, Escape, a bare modifier) leaves the step open for the blur that follows it.
    if (event.key.length === 1) this.stepping.set(false);
  }

  onChange(scope: Scope, value: string): void {
    if (this.stepping()) return;
    this.emit(scope, value);
  }

  onBlur(scope: Scope, value: string): void {
    this.stepping.set(false);
    this.emit(scope, value);
  }

  /** A pointer pick commits at once, even after the visitor arrowed through the options. */
  onPointerDown(): void {
    this.stepping.set(false);
  }

  private emit(scope: Scope, value: string): void {
    // The placeholder option is disabled, so an empty value is never a scope choice.
    if (value === '') return;
    if (scope === 'org') {
      // Re-selecting the current scope is a no-op navigation (and absorbs a `change` that trails
      // an Enter commit once the URL has caught up).
      if (value === this.currentOrg()?.id) return;
      this.orgSelected.emit(value);
      return;
    }
    if (value === this.currentProject()?.id) return;
    this.projectSelected.emit(value);
  }
}
