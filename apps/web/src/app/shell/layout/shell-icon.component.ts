import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import type { NavIconName } from '../nav/nav.models';

/** Static SVG geometry copied from v2 assets/evidgate.js ICONS; never inserts HTML strings. */
@Component({
  selector: 'app-shell-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <svg
      class="h-full w-full"
      viewBox="0 0 24 24"
      fill="none"
      stroke="currentColor"
      stroke-width="1.6"
      [attr.stroke-linecap]="'round'"
      [attr.stroke-linejoin]="'round'"
      aria-hidden="true"
      focusable="false"
    >
      @switch (name()) {
        @case ('tree') {
          <path [attr.d]="'M12 3v5'" />
          <rect x="8.5" y="8" width="7" height="4" rx="1" />
          <path [attr.d]="'M6 15v-3h12v3'" />
          <rect x="3.5" y="15" width="5" height="4" rx="1" />
          <rect x="15.5" y="15" width="5" height="4" rx="1" />
        }
        @case ('sparkle') {
          <path [attr.d]="'m12 3 1.6 4.4L18 9l-4.4 1.6L12 15l-1.6-4.4L6 9l4.4-1.6L12 3Z'" />
          <path [attr.d]="'m18.5 15 .8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8.8-2.2Z'" />
        }
        @case ('layers') {
          <path [attr.d]="'m12 3 9 5-9 5-9-5 9-5Z'" />
          <path [attr.d]="'m3 12 9 5 9-5m-18 4 9 5 9-5'" />
        }
        @case ('rocket') {
          <path [attr.d]="'M14 4c3 1 5 3 6 6-3 1-6 3-8 6l-4-4c3-2 5-5 6-8Z'" />
          <path [attr.d]="'m8 12-3 1 2 2-3 4 4-3 2 2 1-3'" />
          <circle cx="15" cy="8" r="1" />
        }
        @case ('terminal') {
          <rect x="3" y="4" width="18" height="16" rx="2" />
          <path [attr.d]="'m7 9 3 3-3 3m6 0h4'" />
        }
        @case ('list') {
          <path [attr.d]="'M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01'" />
        }
        @case ('map') {
          <path [attr.d]="'m3 6 6-2 6 2 6-2v14l-6 2-6-2-6 2V6ZM9 4v14M15 6v14'" />
        }
        @case ('play') {
          <circle cx="12" cy="12" r="8.5" />
          <path [attr.d]="'m10 8.5 5 3.5-5 3.5v-7Z'" />
        }
        @case ('alert') {
          <path [attr.d]="'M12 3.5 21 19H3l9-15.5ZM12 9v4.5M12 16.5h.01'" />
        }
        @case ('wrench') {
          <path
            [attr.d]="'M14.5 6.5a3.5 3.5 0 0 0-4.6 4.6L4 17l3 3 5.9-5.9a3.5 3.5 0 0 0 4.6-4.6l-2.2 2.2-2.3-2.3 2.5-2.9Z'"
          />
        }
        @case ('book') {
          <path
            [attr.d]="'M4 5.5A2.5 2.5 0 0 1 6.5 3H20v16H6.5A2.5 2.5 0 0 0 4 21.5v-16ZM8 7h8M8 11h6'"
          />
        }
        @case ('shield-check') {
          <path [attr.d]="'M12 3 20 6v6c0 5-3.5 8-8 9.5C7.5 20 4 17 4 12V6l8-3Z'" />
          <path [attr.d]="'m8.5 12 2.5 2.5 4.5-5'" />
        }
        @case ('sliders') {
          <path [attr.d]="'M4 6h7M15 6h5M4 12h3M11 12h9M4 18h9M17 18h3'" />
          <circle cx="13" cy="6" r="2" />
          <circle cx="9" cy="12" r="2" />
          <circle cx="15" cy="18" r="2" />
        }
        @case ('package') {
          <path [attr.d]="'m12 3 8 4.5v9L12 21l-8-4.5v-9L12 3Z'" />
          <path [attr.d]="'m4 7.5 8 4.5 8-4.5M12 12v9'" />
        }
        @case ('chart') {
          <path [attr.d]="'M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-6'" />
        }
        @case ('scroll') {
          <path
            [attr.d]="'M6 3h10a2 2 0 0 1 2 2v14a2 2 0 0 0 2 2H6a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2ZM8 7h6M8 11h6M8 15h4'"
          />
        }
        @case ('server') {
          <rect x="3" y="4" width="18" height="6" rx="1.5" />
          <rect x="3" y="14" width="18" height="6" rx="1.5" />
          <path [attr.d]="'M7 7h.01M7 17h.01'" />
        }
        @case ('settings') {
          <circle cx="12" cy="12" r="3" />
          <path
            [attr.d]="'M12 3v2M12 19v2M3 12h2M19 12h2M5.6 5.6 7 7M17 17l1.4 1.4M18.4 5.6 17 7M7 17l-1.4 1.4'"
          />
        }
        @case ('users') {
          <circle cx="9" cy="9" r="3" />
          <path [attr.d]="'M3 20a6 6 0 0 1 12 0M16 6a3 3 0 0 1 0 6M17 20a6 6 0 0 0-2-4.5'" />
        }
        @case ('credit') {
          <rect x="3" y="5" width="18" height="14" rx="2" />
          <path [attr.d]="'M3 10h18M7 15h4'" />
        }
        @case ('audit') {
          <path [attr.d]="'M7 4h10v16H7zM10 8h4M10 12h4M10 16h4'" />
        }
        @case ('key') {
          <circle cx="8" cy="12" r="3" />
          <path [attr.d]="'M11 12h9l-2 2m0 0-2-2'" />
        }
        @case ('shield-alert') {
          <path
            [attr.d]="'M12 3 20 6v6c0 5-3.5 8-8 9.5C7.5 20 4 17 4 12V6l8-3ZM12 8v4M12 15.5h.01'"
          />
        }
        @case ('life') {
          <circle cx="12" cy="12" r="9" />
          <circle cx="12" cy="12" r="3.5" />
          <path [attr.d]="'m5.5 5.5 3 3M18.5 5.5l-3 3M5.5 18.5l3-3M18.5 18.5l-3-3'" />
        }
        @case ('bot') {
          <rect x="4" y="8" width="16" height="11" rx="2.5" />
          <path [attr.d]="'M12 4v4M8 13h.01M16 13h.01M9.5 16h5'" />
        }
        @case ('plug') {
          <path [attr.d]="'M9 3v4M15 3v4M6 7h12v4a6 6 0 0 1-12 0V7ZM12 17v4'" />
        }
        @case ('home') {
          <path [attr.d]="'m3 11 9-7 9 7M5 10v10h14V10M10 20v-6h4v6'" />
        }
        @case ('compass') {
          <circle cx="12" cy="12" r="9" />
          <path [attr.d]="'m15.5 8.5-2 5-5 2 2-5 5-2Z'" />
        }
        @case ('menu') {
          <path [attr.d]="'M4 7h16M4 12h16M4 17h16'" />
        }
        @case ('close') {
          <path [attr.d]="'m6 6 12 12M18 6 6 18'" />
        }
        @case ('sun') {
          <circle cx="12" cy="12" r="4" />
          <path
            [attr.d]="'M12 2v2M12 20v2M2 12h2M20 12h2M4.9 4.9l1.4 1.4M17.7 17.7l1.4 1.4M19.1 4.9l-1.4 1.4M6.3 17.7l-1.4 1.4'"
          />
        }
        @case ('moon') {
          <path [attr.d]="'M20 14.5A8.5 8.5 0 0 1 9.5 4 8.5 8.5 0 1 0 20 14.5Z'" />
        }
        @case ('chevron-down') {
          <path [attr.d]="'m6 9 6 6 6-6'" />
        }
      }
    </svg>
  `,
  host: { class: 'inline-flex size-(--eg-text-lg) shrink-0' },
})
export class ShellIconComponent {
  readonly name = input.required<
    NavIconName | 'menu' | 'close' | 'sun' | 'moon' | 'chevron-down'
  >();
}
