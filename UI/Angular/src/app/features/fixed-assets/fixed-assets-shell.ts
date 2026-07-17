import { ChangeDetectionStrategy, Component } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';

@Component({
  selector: 'app-fixed-assets-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'contents' },
  imports: [RouterLink, RouterLinkActive, RouterOutlet],
  template: `
    <div class="flex flex-col gap-0 flex-1 min-h-0">
      <nav class="flex gap-1 border-b border-border px-4 pt-4 shrink-0">
        <a routerLink="assets" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-assets">Assets</a>
        <a routerLink="depreciation-runs" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-runs">Depreciation runs</a>
        <a routerLink="disposals" routerLinkActive="border-b-2 border-primary font-semibold text-foreground" [routerLinkActiveOptions]="{ exact: false }"
           class="px-3 py-2 text-sm rounded-t text-muted-foreground hover:text-foreground transition-colors" data-testid="tab-disposals">Disposals</a>
      </nav>
      <router-outlet />
    </div>
  `,
})
export class FixedAssetsShell {}
