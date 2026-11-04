import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { HlmCardImports } from '@evidgate/ui/card';
import { HlmBadge } from '@evidgate/ui/badge';
import type { RolePreview } from './role-preview';
import { MEMBERS_COPY } from './member-list.component';

@Component({
  selector: 'app-role-scope-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [HlmCardImports, HlmBadge],
  host: { class: 'block min-w-0' },
  template: `
    <section
      hlmCard
      class="gap-3 bg-secondary p-3 wrap-anywhere [&_p]:m-0 [&_p]:text-muted-foreground [&_ul]:m-0 [&_ul]:ps-4 [&_code]:font-mono"
      data-role-preview
      [attr.data-preview-kind]="preview().kind"
      [attr.aria-live]="'polite'"
      aria-labelledby="members-preview-title"
    >
      <h3 hlmCardTitle id="members-preview-title">{{ copy.preview }}</h3>
      @if (preview(); as value) {
        @if (value.kind === 'ready') {
          <p>{{ value.role.name }}</p>
          @for (group of value.groups; track group.resource) {
            <h4 class="m-0 text-sm">{{ group.resource }}</h4>
            <ul>
              @for (permission of group.permissions; track permission.code) {
                <li>
                  <code>{{ permission.code }}</code> — {{ permission.description }}
                  @if (permission.sensitive) {
                    <strong
                      hlmBadge
                      [variant]="'outline'"
                      class="sensitive whitespace-normal bg-[var(--eg-warning-bg)] text-[var(--eg-warning-fg)]"
                      ><span aria-hidden="true">⚠</span> {{ copy.sensitive }}</strong
                    >
                  }
                </li>
              }
            </ul>
          }
          @if (value.omitted > 0) {
            <p>{{ copy.omitted }} {{ value.omitted }}</p>
          }
        } @else {
          <p>{{ copy.unavailable }}</p>
        }
      }
      <p>{{ copy.authority }}</p>
    </section>
  `,
})
export class RoleScopePreviewComponent {
  readonly preview = input.required<RolePreview>();
  protected readonly copy = MEMBERS_COPY;
}
