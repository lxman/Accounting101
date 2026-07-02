import { Injectable, computed, effect, inject, signal } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';
import { NAV, navLeafPaths } from './nav';

/**
 * Where a nav path lives in the tree: which section, and which submenu should be open.
 * Landing on a parent's OWN page opens that parent's submenu (so its children are visible);
 * landing on a child opens the child's parent; a childless top item opens no submenu.
 */
export function locate(path: string): { section: string; parent: string | null } | null {
  for (const section of NAV) {
    for (const item of section.items) {
      if (item.path === path) return { section: section.label, parent: item.children ? item.path : null };
      if (item.children?.some((c) => c.path === path)) return { section: section.label, parent: item.path };
    }
  }
  return null;
}

/**
 * Single source of truth for sidebar open/closed state. Accordion semantics: at most ONE
 * section open and at most ONE submenu open at a time. Navigation reconciles the state to the
 * active route (the section/submenu holding the current page opens; everything else closes),
 * so jumping around the menus can never accumulate multiple open sections.
 */
@Injectable({ providedIn: 'root' })
export class NavStateService {
  private readonly router = inject(Router);
  private readonly leafPaths = navLeafPaths();

  private readonly url = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map((e) => e.urlAfterRedirects),
    ),
    { initialValue: this.router.url },
  );

  /** Highlighted item = longest nav leaf path that prefixes the current URL. */
  readonly activePath = computed<string | null>(() => {
    const u = this.url();
    return (
      this.leafPaths
        .filter((p) => u === p || u.startsWith(p + '/'))
        .sort((a, b) => b.length - a.length)[0] ?? null
    );
  });

  private readonly _openSection = signal<string | null>(null);
  private readonly _openParent = signal<string | null>(null);

  constructor() {
    // Navigation is the source of truth for "where I am": open the section (and submenu)
    // that holds the active route, closing whatever else was open.
    effect(() => {
      const a = this.activePath();
      if (!a) return;
      const loc = locate(a);
      if (!loc) return;
      this._openSection.set(loc.section);
      this._openParent.set(loc.parent);
    });
  }

  isSectionOpen(label: string): boolean { return this._openSection() === label; }
  isParentOpen(path: string): boolean { return this._openParent() === path; }

  /** Toggle a section; opening one closes any other section and clears the open submenu. */
  toggleSection(label: string): void {
    this._openSection.update((cur) => (cur === label ? null : label));
    this._openParent.set(null);
  }

  /** Toggle a submenu within the open section; opening one closes any other submenu. */
  toggleParent(path: string): void {
    this._openParent.update((cur) => (cur === path ? null : path));
  }
}
