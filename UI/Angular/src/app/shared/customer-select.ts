import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { ReceivablesService } from '../core/receivables/receivables.service';

/** The customer picker shared by the Receivables list tabs. Bound to the service's persisted
 *  per-client selection, so choosing a customer on one tab carries to the others. */
@Component({
  selector: 'app-customer-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmSelectImports],
  template: `
    <div hlmSelect [value]="svc.selectedCustomerId()" [itemToString]="toName"
         (valueChange)="svc.setSelectedCustomer($any($event) ?? '')">
      <hlm-select-trigger class="w-64">
        <hlm-select-value placeholder="Select a customer" />
      </hlm-select-trigger>
      <hlm-select-content *hlmSelectPortal>
        @for (c of svc.customers(); track c.id) {
          <hlm-select-item [value]="c.id">{{ c.name }}</hlm-select-item>
        }
      </hlm-select-content>
    </div>
  `,
})
export class CustomerSelect {
  readonly svc = inject(ReceivablesService);
  /** id→name for the trigger (so it shows the name, not the raw GUID); falls back to the id. */
  readonly toName = (id: string): string => this.svc.customerName(id);
}
