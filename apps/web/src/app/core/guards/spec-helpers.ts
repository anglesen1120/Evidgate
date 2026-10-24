// 09.1.1-T4.1: Angular 22's `CanMatchFn` takes a third argument — the partial route snapshot of
// the match so far (`route`, `segments`, `currentSnapshot`). The shell's guards decide from the
// session store and the segments only, so every guard spec passes this one empty snapshot instead
// of restating ten fields per call site.
//
// The file is not a `.spec.ts`: it is a spec dependency the application project never reaches
// (`tsconfig.app.json` compiles from `src/main.ts`), so it cannot end up in the bundle.

import { convertToParamMap, type PartialMatchRouteSnapshot } from '@angular/router';

/** An empty match snapshot: no config, no URL, no params — nothing a guard here reads. */
export const EMPTY_MATCH_SNAPSHOT: PartialMatchRouteSnapshot = {
  routeConfig: null,
  url: [],
  params: {},
  queryParams: {},
  fragment: null,
  data: {},
  outlet: 'primary',
  title: undefined,
  paramMap: convertToParamMap({}),
  queryParamMap: convertToParamMap({}),
};
