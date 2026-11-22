import { ComponentHarness } from '@angular/cdk/testing';
import { TestbedHarnessEnvironment } from '@angular/cdk/testing/testbed';
import { TestBed } from '@angular/core/testing';

import { ReleaseEvidenceContextComponent } from './release-evidence-context.component';
import { RELEASE_VIEW } from './release-view.fixture';
import type { ReleaseReviewState } from './release-view-state';

class EvidenceHarness extends ComponentHarness {
  static hostSelector = 'app-release-evidence-context';
  readonly text = async () => (await this.host()).text();
}

async function content(state: ReleaseReviewState, view = RELEASE_VIEW) {
  const fixture = TestBed.createComponent(ReleaseEvidenceContextComponent);
  fixture.componentRef.setInput('view', view);
  fixture.componentRef.setInput('state', state);
  fixture.detectChanges();
  return (await TestbedHarnessEnvironment.harnessForFixture(fixture, EvidenceHarness)).text();
}

describe('ReleaseEvidenceContextComponent', () => {
  it.each(['default', 'newer', 'expired'] as const)(
    'keeps the original pins under %s',
    async (state) => {
      const text = await content(state);
      expect(text).toContain('rd-14 pins run-1844 / ev-4');
      expect(text).toContain('PASS remains pinned to ev-2');
      expect(text).toContain('A missing worker cannot contribute a pass.');
      expect(text).toContain('ph-114a…a38e');
    },
  );

  it('renders markup-like policy identifiers as literal text', async () => {
    const policy = '<em>policy markup</em>';
    expect(await content('default', { ...RELEASE_VIEW, policy })).toContain(policy);
  });

  it('withholds worker details after a partial read', async () => {
    const text = await content('partial');
    expect(text).toContain('Component details unavailable');
    expect(text).not.toContain('A missing worker cannot contribute a pass.');
    expect(text).toContain('rd-14 pins run-1844 / ev-4');
  });
});
