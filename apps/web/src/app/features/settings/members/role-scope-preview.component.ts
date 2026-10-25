import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { RolePreview } from './role-preview';
import { MEMBERS_COPY } from './member-list.component';

@Component({
  selector: 'app-role-scope-preview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  styleUrl: './members.page.css',
  template: `
    <section
      data-role-preview
      [attr.data-preview-kind]="preview().kind"
      [attr.aria-live]="'polite'"
      aria-labelledby="members-preview-title"
    >
      <h3 id="members-preview-title">{{ copy.preview }}</h3>
      @if (preview(); as value) {
        @if (value.kind === 'ready') {
          <p>{{ value.role.name }}</p>
          @for (group of value.groups; track group.resource) {
            <h4>{{ group.resource }}</h4>
            <ul>
              @for (permission of group.permissions; track permission.code) {
                <li>
                  <code>{{ permission.code }}</code> — {{ permission.description }}
                  @if (permission.sensitive) {
                    <strong class="sensitive"
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
