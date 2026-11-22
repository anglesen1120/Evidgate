import { galleryCopy as c } from './studio-gallery.copy';

export type TemplateSource = 'built' | 'team';
export interface GalleryTemplateViewModel {
  readonly id: 'checkout' | 'api' | 'unit';
  readonly source: TemplateSource;
  readonly title: string;
  readonly engine: string;
  readonly illustration: readonly [string, string];
  readonly intent: string;
  readonly precondition: string;
  readonly action: string;
  readonly assertion: string;
  readonly cleanup: string;
  readonly draftName: string;
  readonly variant: string;
}
export interface StudioGalleryViewModel {
  readonly scope: string;
  readonly templates: readonly GalleryTemplateViewModel[];
}
export const galleryFixture: StudioGalleryViewModel = {
  scope: c.scope,
  templates: [
    {
      id: 'checkout',
      source: 'built',
      engine: c.playwright,
      title: c.checkoutTitle,
      illustration: [c.checkoutArt, c.receipt],
      intent: c.checkoutIntent,
      precondition: c.checkoutBefore,
      action: c.checkoutAction,
      assertion: c.checkoutAssertion,
      cleanup: c.checkoutCleanup,
      draftName: c.checkoutName,
      variant: c.browserVariant,
    },
    {
      id: 'api',
      source: 'built',
      engine: c.api,
      title: c.apiTitle,
      illustration: [c.apiArt, c.denialArt],
      intent: c.apiIntent,
      precondition: c.apiBefore,
      action: c.apiAction,
      assertion: c.apiAssertion,
      cleanup: c.apiCleanup,
      draftName: c.apiTitle,
      variant: c.api,
    },
    {
      id: 'unit',
      source: 'team',
      engine: c.unit,
      title: c.unitTitle,
      illustration: [c.unitArt, c.roundArt],
      intent: c.unitIntent,
      precondition: c.unitBefore,
      action: c.unitAction,
      assertion: c.unitAssertion,
      cleanup: c.unitCleanup,
      draftName: c.unitTitle,
      variant: c.unit,
    },
  ],
};
