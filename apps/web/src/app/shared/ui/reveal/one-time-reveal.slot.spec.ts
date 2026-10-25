// 09.2.1-T2.2: the holder is local to its owner; no root service or persisted credential.
import { Component, inject, provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';

import { RevealSlot, type RevealEntry, type RevealSource } from './one-time-reveal.slot';

@Component({
  standalone: true,
  template: '',
  providers: [RevealSlot],
})
class RevealOwner {
  readonly reveal = inject(RevealSlot);
}

describe('RevealSlot', () => {
  it('starts empty and exposes a read-only signal', () => {
    const slot = new RevealSlot();
    expect(slot.current()).toBeNull();
    expect('set' in slot.current).toBe(false);
    expect('update' in slot.current).toBe(false);
  });

  it.each<RevealSource>(['binding-challenge', 'ingest-token', 'ingest-token-rotation'])(
    'shows and replaces a %s value rather than accumulating credentials',
    (source) => {
      const slot = new RevealSlot();
      const first: RevealEntry = { source, value: 'first-one-time-value' };
      const replacement: RevealEntry = { source, value: 'replacement-one-time-value' };
      slot.show(first);
      expect(slot.current()).toBe(first);
      slot.show(replacement);
      expect(slot.current()).toBe(replacement);
      slot.clear();
      expect(slot.current()).toBeNull();
    },
  );

  it('clears idempotently before and after showing a credential', () => {
    const slot = new RevealSlot();
    slot.clear();
    slot.clear();
    expect(slot.current()).toBeNull();
    slot.show({ source: 'ingest-token', value: 'one-time-value' });
    slot.clear();
    slot.clear();
    expect(slot.current()).toBeNull();
  });

  it('clears on ngOnDestroy even when called repeatedly', () => {
    const slot = new RevealSlot();
    slot.show({ source: 'binding-challenge', value: 'one-time-value' });
    slot.ngOnDestroy();
    slot.ngOnDestroy();
    expect(slot.current()).toBeNull();
  });

  it('isolates mounted owners and clears only the destroyed owner through Angular DI', async () => {
    await TestBed.configureTestingModule({
      imports: [RevealOwner],
      providers: [provideZonelessChangeDetection()],
    }).compileComponents();
    const first = TestBed.createComponent(RevealOwner);
    const second = TestBed.createComponent(RevealOwner);
    const firstSlot = first.componentInstance.reveal;
    const secondSlot = second.componentInstance.reveal;
    expect(firstSlot).not.toBe(secondSlot);
    firstSlot.show({ source: 'ingest-token', value: 'first-owner-value' });
    secondSlot.show({ source: 'binding-challenge', value: 'second-owner-value' });
    first.destroy();
    expect(firstSlot.current()).toBeNull();
    expect(secondSlot.current()?.value).toBe('second-owner-value');
    second.destroy();
    expect(secondSlot.current()).toBeNull();
  });
});
