import { openFullValuePopover } from './full-value-popover';

const px = (value: string) => Number.parseFloat(value) || Infinity;

function open(anchor: DOMRect, boundary: DOMRect) {
  const trigger = document.createElement('button');
  const above = document.createElement('span');
  const within = document.createElement('div');
  const panel = document.createElement('span');
  panel.popover = 'manual';
  within.append(trigger, panel);
  trigger.append(above);
  document.body.append(within);
  vi.spyOn(document.documentElement, 'clientWidth', 'get').mockReturnValue(390);
  vi.spyOn(document.documentElement, 'clientHeight', 'get').mockReturnValue(600);
  vi.spyOn(trigger, 'getBoundingClientRect').mockReturnValue(new DOMRect(anchor.x, 0, 72, 600));
  vi.spyOn(above, 'getBoundingClientRect').mockReturnValue(anchor);
  vi.spyOn(within, 'getBoundingClientRect').mockReturnValue(boundary);
  vi.spyOn(panel, 'getBoundingClientRect').mockImplementation(
    () =>
      new DOMRect(
        0,
        0,
        Math.min(160, px(panel.style.maxWidth)),
        Math.min(100, px(panel.style.maxHeight)),
      ),
  );
  const close = openFullValuePopover(trigger, panel, vi.fn(), { above, within });
  return { panel, close };
}

describe('bounded above popover placement', () => {
  afterEach(() => {
    document.body.replaceChildren();
    vi.restoreAllMocks();
  });

  it.each([
    ['centres above the bar', 150, 106],
    ['clamps at the left edge', 20, 20],
    ['clamps at the right edge', 298, 210],
  ] as const)('%s', (_, x, expectedLeft) => {
    const { panel, close } = open(new DOMRect(x, 200, 72, 150), new DOMRect(20, 0, 350, 500));
    expect(panel.style.top).toBe('100px');
    expect(panel.style.left).toBe(`${expectedLeft}px`);
    close();
  });

  it('uses the side with room when the full panel cannot fit above', () => {
    const { panel, close } = open(new DOMRect(220, 30, 72, 150), new DOMRect(20, 0, 350, 500));
    expect(panel.style.left).toBe('60px');
    expect(panel.style.top).toBe('30px');
    close();
  });

  it('keeps a scrollable panel above when neither side has room', () => {
    const { panel, close } = open(new DOMRect(20, 70, 72, 150), new DOMRect(20, 0, 72, 500));
    expect(panel.style.left).toBe('20px');
    expect(panel.style.top).toBe('0px');
    expect(panel.style.maxHeight).toBe('70px');
    close();
  });

  it.each([
    ['the painted bar starts below the viewport', 605],
    ['there is no room on either side or above', 0],
  ] as const)('does not show the panel when %s', (_, y) => {
    const { panel, close } = open(new DOMRect(20, y, 72, 150), new DOMRect(20, 0, 72, 800));
    expect(panel.style.visibility).toBe('hidden');
    close();
    expect(panel.style.visibility).toBe('');
  });
});
