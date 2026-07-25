import { Injectable, Signal, effect, inject } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter, map } from 'rxjs';
import { CapabilityService } from './capability.service';

interface RouteCap { requiredCapability?: string; fallback?: string; requiredModule?: string; }

/** Redirects the user off the current page the instant its required capability or module leaves the
 * caps signal — the reactive complement to the navigation-time canWrite/moduleEnabled guards.
 * Root-level; started by being injected at bootstrap (see app.ts). */
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
      const { requiredCapability, fallback, requiredModule } = this.activeCap();
      if (!this.caps.loaded()) return;
      const lost =
        (!!requiredCapability && !this.caps.has(requiredCapability)) ||
        (!!requiredModule && !this.caps.moduleEnabled(requiredModule));
      if (lost) void this.router.navigateByUrl(fallback ?? '/dashboard');
    });
  }
}
