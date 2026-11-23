import { ChangeDetectionStrategy, Component, input } from '@angular/core';

// Static Lucide geometry avoids the string-to-innerHTML sink used by ng-icons.
@Component({
  selector: 'hlm-icon',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { 'aria-hidden': 'true', class: 'inline-flex shrink-0' },
  template: `
    <svg
      [attr.viewBox]="'0 0 24 24'"
      [attr.fill]="'none'"
      [attr.stroke]="'currentColor'"
      [attr.stroke-width]="1.65"
      [attr.stroke-linecap]="'round'"
      [attr.stroke-linejoin]="'round'"
      [attr.focusable]="'false'"
      class="size-full"
    >
      @switch (name()) {
        @case ('lucideActivity') {
          <path
            [attr.d]="'M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2'"
          ></path>
        }
        @case ('lucideCheck') {
          <path [attr.d]="'M20 6 9 17l-5-5'"></path>
        }
        @case ('lucideChevronDown') {
          <path [attr.d]="'m6 9 6 6 6-6'"></path>
        }
        @case ('lucideChevronLeft') {
          <path [attr.d]="'m15 18-6-6 6-6'"></path>
        }
        @case ('lucideChevronRight') {
          <path [attr.d]="'m9 18 6-6-6-6'"></path>
        }
        @case ('lucideChevronUp') {
          <path [attr.d]="'m18 15-6-6-6 6'"></path>
        }
        @case ('lucideCircle') {
          <circle [attr.cx]="'12'" [attr.cy]="'12'" [attr.r]="'10'"></circle>
        }
        @case ('lucideCircleCheck') {
          <circle [attr.cx]="'12'" [attr.cy]="'12'" [attr.r]="'10'"></circle>
          <path [attr.d]="'m9 12 2 2 4-4'"></path>
        }
        @case ('lucideClock') {
          <path [attr.d]="'M12 6v6l4 2'"></path>
          <circle [attr.cx]="'12'" [attr.cy]="'12'" [attr.r]="'10'"></circle>
        }
        @case ('lucideCopy') {
          <rect
            [attr.width]="14"
            [attr.height]="14"
            [attr.x]="8"
            [attr.y]="8"
            [attr.rx]="2"
            [attr.ry]="2"
          ></rect>
          <path [attr.d]="'M4 16c-1.1 0-2-.9-2-2V4c0-1.1.9-2 2-2h10c1.1 0 2 .9 2 2'"></path>
        }
        @case ('lucideEllipsis') {
          <circle cx="12" cy="12" r="1" />
          <circle cx="19" cy="12" r="1" />
          <circle cx="5" cy="12" r="1" />
        }
        @case ('lucideEye') {
          <path
            [attr.d]="'M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0'"
          />
          <circle cx="12" cy="12" r="3" />
        }
        @case ('lucideFileDiff') {
          <path
            [attr.d]="'M6 22a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h8a2.4 2.4 0 0 1 1.704.706l3.588 3.588A2.4 2.4 0 0 1 20 8v12a2 2 0 0 1-2 2z'"
          />
          <path [attr.d]="'M9 10h6'" />
          <path [attr.d]="'M12 13V7'" />
          <path [attr.d]="'M9 17h6'" />
        }
        @case ('lucideGitMerge') {
          <circle cx="18" cy="18" r="3" />
          <circle cx="6" cy="6" r="3" />
          <path [attr.d]="'M6 21V9a9 9 0 0 0 9 9'" />
        }
        @case ('lucideInbox') {
          <path [attr.d]="'M22 12h-6l-2 3h-4l-2-3H2'"></path>
          <path
            [attr.d]="'m5.45 5.11-3.45 6.89v6a2 2 0 0 0 2 2h16a2 2 0 0 0 2-2v-6l-3.45-6.89A2 2 0 0 0 16.76 4H7.24a2 2 0 0 0-1.79 1.11z'"
          ></path>
        }
        @case ('lucideInfo') {
          <circle [attr.cx]="'12'" [attr.cy]="'12'" [attr.r]="'10'"></circle>
          <path [attr.d]="'M12 16v-4'"></path>
          <path [attr.d]="'M12 8h.01'"></path>
        }
        @case ('lucideLoader2') {
          <path [attr.d]="'M21 12a9 9 0 1 1-6.219-8.56'"></path>
        }
        @case ('lucideMaximize2') {
          <path [attr.d]="'M15 3h6v6'"></path>
          <path [attr.d]="'m21 3-7 7'"></path>
          <path [attr.d]="'m3 21 7-7'"></path>
          <path [attr.d]="'M9 21H3v-6'"></path>
        }
        @case ('lucideOctagonX') {
          <path [attr.d]="'m15 9-6 6'"></path>
          <path
            [attr.d]="'M2.586 16.726A2 2 0 0 1 2 15.312V8.688a2 2 0 0 1 .586-1.414l4.688-4.688A2 2 0 0 1 8.688 2h6.624a2 2 0 0 1 1.414.586l4.688 4.688A2 2 0 0 1 22 8.688v6.624a2 2 0 0 1-.586 1.414l-4.688 4.688a2 2 0 0 1-1.414.586H8.688a2 2 0 0 1-1.414-.586z'"
          ></path>
          <path [attr.d]="'m9 9 6 6'"></path>
        }
        @case ('lucidePaperclip') {
          <path
            [attr.d]="'m16 6-8.414 8.586a2 2 0 0 0 2.829 2.829l8.414-8.586a4 4 0 1 0-5.657-5.657l-8.379 8.551a6 6 0 1 0 8.485 8.485l8.379-8.551'"
          />
        }
        @case ('lucidePencil') {
          <path
            [attr.d]="'M21.174 6.812a1 1 0 0 0-3.986-3.987L3.842 16.174a2 2 0 0 0-.5.83l-1.321 4.352a.5.5 0 0 0 .623.622l4.353-1.32a2 2 0 0 0 .83-.497z'"
          />
          <path [attr.d]="'m15 5 4 4'" />
        }
        @case ('lucideRotateCw') {
          <path [attr.d]="'M21 12a9 9 0 1 1-9-9c2.52 0 4.93 1 6.74 2.74L21 8'" />
          <path [attr.d]="'M21 3v5h-5'" />
        }
        @case ('lucideTriangleAlert') {
          <path
            [attr.d]="'m21.73 18-8-14a2 2 0 0 0-3.48 0l-8 14A2 2 0 0 0 4 21h16a2 2 0 0 0 1.73-3'"
          ></path>
          <path [attr.d]="'M12 9v4'"></path>
          <path [attr.d]="'M12 17h.01'"></path>
        }
        @case ('lucideX') {
          <path [attr.d]="'M18 6 6 18'"></path>
          <path [attr.d]="'m6 6 12 12'"></path>
        }
        @case ('navAudit') {
          <path [attr.d]="'M7 4h10v16H7zM10 8h4M10 12h4M10 16h4'" />
        }
        @case ('navBot') {
          <rect x="4" y="8" width="16" height="11" rx="2.5" />
          <path [attr.d]="'M12 4v4M8 13h.01M16 13h.01M9.5 16h5'" />
        }
        @case ('navChart') {
          <path [attr.d]="'M4 20V4M4 20h16M8 16v-4M12 16V8M16 16v-6'" />
        }
        @case ('navCode') {
          <path [attr.d]="'m8 7-5 5 5 5m8-10 5 5-5 5m-3-14-2 18'" />
        }
        @case ('navFile') {
          <path [attr.d]="'M6 3h8l4 4v14H6z M14 3v5h4 M9 12h6m-6 4h5'" />
        }
        @case ('navFolder') {
          <path [attr.d]="'M3 6h6l2 2h10v12H3z'" />
        }
        @case ('navGrid') {
          <path [attr.d]="'M3 3h7v7H3z M14 3h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z'" />
        }
        @case ('navInbox') {
          <path [attr.d]="'M3 15l3-10h12l3 10v4H3z M3 15h5l2 2h4l2-2h5'" />
        }
        @case ('navKey') {
          <circle cx="8" cy="12" r="3" />
          <path [attr.d]="'M11 12h9l-2 2m0 0-2-2'" />
        }
        @case ('navLife') {
          <circle cx="12" cy="12" r="9" />
          <circle cx="12" cy="12" r="3.5" />
          <path [attr.d]="'m5.5 5.5 3 3M18.5 5.5l-3 3M5.5 18.5l3-3M18.5 18.5l-3-3'" />
        }
        @case ('navList') {
          <path [attr.d]="'M8 6h12M8 12h12M8 18h12M4 6h.01M4 12h.01M4 18h.01'" />
        }
        @case ('navMap') {
          <path [attr.d]="'m3 6 6-2 6 2 6-2v14l-6 2-6-2-6 2V6ZM9 4v14M15 6v14'" />
        }
        @case ('navPeople') {
          <path
            [attr.d]="'M3 20v-2a5 5 0 0 1 10 0v2 M16 14a5 5 0 0 1 5 5 M11 7a3 3 0 1 1-6 0 3 3 0 0 1 6 0 M15 4a3 3 0 0 1 0 6'"
          />
        }
        @case ('navPlug') {
          <path [attr.d]="'M9 3v4M15 3v4M6 7h12v4a6 6 0 0 1-12 0V7ZM12 17v4'" />
        }
        @case ('navQueue') {
          <path [attr.d]="'M4 6h13M4 12h10M4 18h7m6-5 3 3-3 3m-4-3h7'" />
        }
        @case ('navRun') {
          <path [attr.d]="'M5 3h14v18H5z M10 8l5 4-5 4z'" />
        }
        @case ('navServer') {
          <rect x="3" y="4" width="18" height="6" rx="1.5" />
          <rect x="3" y="14" width="18" height="6" rx="1.5" />
          <path [attr.d]="'M7 7h.01M7 17h.01'" />
        }
        @case ('navSettings') {
          <path [attr.d]="'M4 7h10m4 0h2M4 17h2m4 0h10 M14 4v6M8 14v6'" />
        }
        @case ('navShield') {
          <path [attr.d]="'M12 2l8 3v6c0 5-4 9-8 11-4-2-8-6-8-11V5z M8 12l3 3 5-6'" />
        }
        @case ('navShieldAlert') {
          <path
            [attr.d]="'M12 3 20 6v6c0 5-3.5 8-8 9.5C7.5 20 4 17 4 12V6l8-3ZM12 8v4M12 15.5h.01'"
          />
        }
        @case ('navShieldCheck') {
          <path [attr.d]="'M12 3 20 6v6c0 5-3.5 8-8 9.5C7.5 20 4 17 4 12V6l8-3Z'" />
          <path [attr.d]="'m8.5 12 2.5 2.5 4.5-5'" />
        }
        @case ('navSliders') {
          <path [attr.d]="'M4 6h7M15 6h5M4 12h3M11 12h9M4 18h9M17 18h3'" />
          <circle cx="13" cy="6" r="2" />
          <circle cx="9" cy="12" r="2" />
          <circle cx="15" cy="18" r="2" />
        }
        @case ('navTree') {
          <path [attr.d]="'M12 3v5'" />
          <rect x="8.5" y="8" width="7" height="4" rx="1" />
          <path [attr.d]="'M6 15v-3h12v3'" />
          <rect x="3.5" y="15" width="5" height="4" rx="1" />
          <rect x="15.5" y="15" width="5" height="4" rx="1" />
        }
      }
    </svg>
  `,
})
export class HlmIcon {
  readonly name = input.required<HlmIconName>();
}

/** `nav` names reproduce shell navigation geometry; `lucide` names use the pinned Lucide set. */
export const HLM_ICON_NAMES = [
  'lucideActivity',
  'lucideCheck',
  'lucideChevronDown',
  'lucideChevronLeft',
  'lucideChevronRight',
  'lucideChevronUp',
  'lucideCircle',
  'lucideCircleCheck',
  'lucideClock',
  'lucideCopy',
  'lucideEllipsis',
  'lucideEye',
  'lucideFileDiff',
  'lucideGitMerge',
  'lucideInbox',
  'lucideInfo',
  'lucideLoader2',
  'lucideMaximize2',
  'lucideOctagonX',
  'lucidePaperclip',
  'lucidePencil',
  'lucideRotateCw',
  'lucideTriangleAlert',
  'lucideX',
  'navAudit',
  'navBot',
  'navChart',
  'navCode',
  'navFile',
  'navFolder',
  'navGrid',
  'navInbox',
  'navKey',
  'navLife',
  'navList',
  'navMap',
  'navPeople',
  'navPlug',
  'navQueue',
  'navRun',
  'navServer',
  'navSettings',
  'navShield',
  'navShieldAlert',
  'navShieldCheck',
  'navSliders',
  'navTree',
] as const;

export type HlmIconName = (typeof HLM_ICON_NAMES)[number];
