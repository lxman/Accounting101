import { Injectable, Signal, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';
import { CapabilityService } from './capability.service';

interface RouteCap { requiredCapability?: string; fallback?: string; }

/** Redirects the user off the current page the instant its required capability leaves the caps
 * signal — the reactive complement to the navigation-time canWrite guard. Root-level; started by
 * being injected at bootstrap (see app.ts). */
@Injectable({ providedIn: 'root' })
export class RouteSentinelService {
  private readonly router = inject(Router);
  private readonly caps = inject(CapabilityService);

  // The deepest active route's capability metadata, refreshed on every completed navigation.
  private readonly activeCap: Signal<RouteCap> = toSignal(
    this.router.events.pipe(
      filter((e): e is NavigationEnd => e instanceof NavigationEnd),
      map(() => {
        let r = this.router.routerState.snapshot.root;
        while (r.firstChild) r = r.firstChild;
        return r.data as RouteCap;
      }),
    ),
    { initialValue: {} as RouteCap },
  );

  constructor() {
    effect(() => {
      const { requiredCapability, fallback } = this.activeCap();
      if (!requiredCapability || !this.caps.loaded()) return;
      if (!this.caps.has(requiredCapability)) {
        void this.router.navigateByUrl(fallback ?? '/dashboard');
      }
    });
  }
}
