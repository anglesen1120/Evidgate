import type { HttpRequest } from '@angular/common/http';
import collecting from '../../../../../../fixtures/web-api/run-detail-collecting/input.json';
import { findFixtureEntry, type FixtureEntry } from './fixture-catalog';

/** This opt-in specimen makes acceptance and the later collection state independently visible. */
export class RunCancelFixtureScenario {
  private pollsAfterAcceptance: number | null = null;

  entry(
    request: HttpRequest<unknown>,
    relativePath: string,
    runVariant: string,
    cancelVariant: string,
  ): FixtureEntry | undefined {
    if (runVariant !== 'detail-cancel-poll') return undefined;
    const path = `/runs/${collecting.body.run_id}`;
    if (request.method === 'POST' && relativePath === `${path}/cancel` && cancelVariant === 'ok') {
      this.pollsAfterAcceptance ??= 0;
      return findFixtureEntry('cancelRun', 'ok');
    }
    if (request.method !== 'GET' || relativePath !== path) return undefined;
    if (this.pollsAfterAcceptance === null) return findFixtureEntry('getRun', 'detail-collecting');
    return findFixtureEntry(
      'getRun',
      this.pollsAfterAcceptance++ === 0 ? 'detail-collecting' : 'detail-canceled',
    );
  }
}
