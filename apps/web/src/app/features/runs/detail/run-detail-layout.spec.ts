import { revealSelectedTab } from './run-detail-layout';

describe('revealSelectedTab', () => {
  it('matches literal values without interpreting them as selectors', () => {
    const strip = document.createElement('div');
    strip.style.paddingInlineStart = '0px';
    Object.defineProperties(strip, { scrollWidth: { value: 300 }, clientWidth: { value: 100 } });
    const tab = document.createElement('button');
    tab.dataset['runTab'] = 'evidence';
    strip.append(tab);
    vi.spyOn(tab, 'getBoundingClientRect').mockReturnValue(new DOMRect(120, 0, 30, 20));
    vi.spyOn(strip, 'getBoundingClientRect').mockReturnValue(new DOMRect(0, 0, 100, 20));
    revealSelectedTab('unknown"]', strip);
    expect(strip.scrollLeft).toBe(0);
    revealSelectedTab('evidence', strip);
    expect(strip.scrollLeft).toBe(50);
  });
});
