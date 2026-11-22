import { TestBed } from '@angular/core/testing';
import { EditorPreviewStore } from './editor-preview.store';

describe('EditorPreviewStore', () => {
  it('keeps independent field edits and clears dirty only when both source values are restored', () => {
    TestBed.configureTestingModule({ providers: [EditorPreviewStore] });
    const store = TestBed.inject(EditorPreviewStore);
    store.change('intent', 'new intent', 'original intent');
    store.change('assertion', '', 'original assertion');
    expect(store.edits()).toEqual({ intent: 'new intent', assertion: '' });
    store.change('intent', 'original intent', 'original intent');
    expect(store.dirty()).toBe(true);
    expect(store.edits()).toEqual({ assertion: '' });
    store.change('assertion', 'original assertion', 'original assertion');
    expect(store.edits()).toEqual({});
    expect(store.dirty()).toBe(false);
  });
});
