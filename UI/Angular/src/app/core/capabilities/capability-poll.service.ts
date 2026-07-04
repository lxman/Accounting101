import { DestroyRef, Injectable, inject } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { interval } from 'rxjs';
import { CapabilityService } from './capability.service';
import { ClientContextService } from '../client/client-context.service';

/** Poll cadence (ms). A future SignalR push could replace this timer without touching consumers. */
export const POLL_INTERVAL_MS = 15000;

/** Gently re-resolves the current user's capabilities on a timer so an IDLE user (making no
 * requests) is still bounced by the sentinel when their access changes. Skips when no client is
 * selected or the tab is hidden. Started by being injected at bootstrap (see app.ts). */
@Injectable({ providedIn: 'root' })
export class CapabilityPollService {
  private readonly caps = inject(CapabilityService);
  private readonly client = inject(ClientContextService);

  constructor() {
    interval(POLL_INTERVAL_MS)
      .pipe(takeUntilDestroyed(inject(DestroyRef)))
      .subscribe(() => {
        if (this.client.clientId() && document.visibilityState === 'visible') {
          this.caps.reload();
        }
      });
  }
}
