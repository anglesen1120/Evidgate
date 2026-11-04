import { isPlatformBrowser } from '@angular/common';
import {
  DestroyRef,
  effect,
  ElementRef,
  HostAttributeToken,
  inject,
  Injector,
  NgZone,
  PLATFORM_ID,
  runInInjectionContext,
} from '@angular/core';
import { type ClassValue, clsx } from 'clsx';
import { twMerge } from 'tailwind-merge';

export function hlm(...inputs: ClassValue[]) {
  return twMerge(clsx(inputs));
}
const elementClassManagers = new WeakMap<HTMLElement, ElementClassManager>();
let globalObserver: MutationObserver | null = null;
const observedElements = new Set<HTMLElement>();

interface ElementClassManager {
  element: HTMLElement;
  sources: Map<number, { classes: Set<string>; order: number }>;
  baseClasses: Set<string>;
  isUpdating: boolean;
  nextOrder: number;
  hasInitialized: boolean;
  restoreRafId: number | null;
  transitionsSuppressed: boolean;
  previousTransition: string;
  previousTransitionPriority: string;
}

let sourceCounter = 0;

export function classes(computed: () => ClassValue[] | string, options: ClassesOptions = {}) {
  runInInjectionContext(options.injector ?? inject(Injector), () => {
    const elementRef = options.elementRef ?? inject<ElementRef<HTMLElement>>(ElementRef);
    const platformId = inject(PLATFORM_ID);
    const destroyRef = inject(DestroyRef);
    const baseClasses = inject(new HostAttributeToken('class'), { optional: true });

    const element = elementRef.nativeElement;
    const sourceId = sourceCounter++;
    const activeManager = getManager(element, baseClasses, platformId);
    const sourceOrder = activeManager.nextOrder++;

    function updateClasses(): void {
      const newClasses = toClassList(computed());
      activeManager.sources.set(sourceId, {
        classes: new Set(newClasses),
        order: sourceOrder,
      });
      updateElement(activeManager);
      if (activeManager.transitionsSuppressed) {
        activeManager.transitionsSuppressed = false;
        activeManager.restoreRafId = requestAnimationFrame(() => {
          activeManager.restoreRafId = null;
          restoreTransitionSuppression(activeManager);
        });
      }
    }
    destroyRef.onDestroy(() => {
      if (activeManager.restoreRafId !== null) {
        cancelAnimationFrame(activeManager.restoreRafId);
        activeManager.restoreRafId = null;
      }

      if (activeManager.transitionsSuppressed) {
        activeManager.transitionsSuppressed = false;
        restoreTransitionSuppression(activeManager);
      }
      activeManager.sources.delete(sourceId);
      if (activeManager.sources.size === 0) {
        cleanupManager(element);
      } else {
        updateElement(activeManager);
      }
    });

    effect(updateClasses);
  });
}

function getManager(
  element: HTMLElement,
  baseClasses: string | null,
  platformId: object,
): ElementClassManager {
  const existing = elementClassManagers.get(element);
  if (existing) return existing;
  const manager: ElementClassManager = {
    element,
    sources: new Map(),
    baseClasses: new Set(baseClasses ? toClassList(baseClasses) : []),
    isUpdating: false,
    nextOrder: 0,
    hasInitialized: false,
    restoreRafId: null,
    transitionsSuppressed: false,
    previousTransition: '',
    previousTransitionPriority: '',
  };
  elementClassManagers.set(element, manager);
  inject(NgZone).runOutsideAngular(() => setupGlobalObserver(platformId));
  observedElements.add(element);
  // Prevent transitions from the unstyled initial render to the first resolved classes.
  if (isPlatformBrowser(platformId)) {
    manager.previousTransition = element.style.getPropertyValue('transition');
    manager.previousTransitionPriority = element.style.getPropertyPriority('transition');
    element.style.setProperty('transition', 'none', 'important');
    manager.transitionsSuppressed = true;
  }
  return manager;
}

function restoreTransitionSuppression(manager: ElementClassManager): void {
  const prev = manager.previousTransition;
  if (prev) {
    manager.element.style.setProperty(
      'transition',
      prev,
      manager.previousTransitionPriority || undefined,
    );
  } else {
    manager.element.style.removeProperty('transition');
  }
}
function setupGlobalObserver(platformId: object): void {
  if (isPlatformBrowser(platformId) && !globalObserver) {
    globalObserver = new MutationObserver((mutations) => {
      for (const mutation of mutations) {
        if (mutation.type === 'attributes' && mutation.attributeName === 'class') {
          const element = mutation.target as HTMLElement;
          const manager = elementClassManagers.get(element);
          if (manager && observedElements.has(element)) {
            if (manager.isUpdating) continue; // Ignore changes we're making
            const currentClasses = toClassList(element.className);
            const allSourceClasses = new Set<string>();
            for (const source of manager.sources.values()) {
              for (const className of source.classes) {
                allSourceClasses.add(className);
              }
            }
            manager.baseClasses.clear();

            for (const className of currentClasses) {
              if (!allSourceClasses.has(className)) {
                manager.baseClasses.add(className);
              }
            }

            updateElement(manager);
          }
        }
      }
    });
    globalObserver.observe(document, {
      attributes: true,
      attributeFilter: ['class'],
      subtree: true, // Watch all descendants
    });
  }
}

function updateElement(manager: ElementClassManager): void {
  if (manager.isUpdating) return; // Prevent recursive updates

  manager.isUpdating = true;
  if (!manager.hasInitialized && manager.sources.size > 0) {
    const currentClasses = toClassList(manager.element.className);
    const allSourceClasses = new Set<string>();
    for (const source of manager.sources.values()) {
      source.classes.forEach((className) => allSourceClasses.add(className));
    }
    currentClasses.forEach((className) => {
      if (!allSourceClasses.has(className)) {
        manager.baseClasses.add(className);
      }
    });

    manager.hasInitialized = true;
  }
  const sortedSources = Array.from(manager.sources.entries()).sort(
    ([, a], [, b]) => a.order - b.order,
  );

  const allSourceClasses: string[] = [];
  for (const [, source] of sortedSources) {
    allSourceClasses.push(...source.classes);
  }
  const classesToApply =
    allSourceClasses.length > 0 || manager.baseClasses.size > 0
      ? hlm([...allSourceClasses, ...manager.baseClasses])
      : '';
  if (manager.element.className !== classesToApply) {
    manager.element.className = classesToApply;
  }

  manager.isUpdating = false;
}

function cleanupManager(element: HTMLElement): void {
  observedElements.delete(element);
  elementClassManagers.delete(element);
  if (observedElements.size === 0 && globalObserver) {
    globalObserver.disconnect();
    globalObserver = null;
  }
}

interface ClassesOptions {
  elementRef?: ElementRef<HTMLElement>;
  injector?: Injector;
}
const classListCache = new Map<string, string[]>();

function toClassList(className: string | ClassValue[]): string[] {
  const cached = typeof className === 'string' ? classListCache.get(className) : undefined;
  if (cached) return cached;

  const result = clsx(className)
    .split(' ')
    .filter((c) => c.length > 0);
  if (typeof className === 'string' && classListCache.size < 1000) {
    classListCache.set(className, result);
  }

  return result;
}
