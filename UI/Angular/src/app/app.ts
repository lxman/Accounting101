import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { Shell } from './layout/shell';
import { ClientContextService } from './core/client/client-context.service';
import { environment } from './core/api/environment';
import { RouteSentinelService } from './core/capabilities/route-sentinel.service';

@Component({
  selector: 'app-root',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [Shell],
  template: `<app-shell />`,
})
export class App {
  constructor() {
    const c = inject(ClientContextService);
    if (environment.devClientId) c.select(environment.devClientId);
    inject(RouteSentinelService);   // start the live route sentinel
  }
}
