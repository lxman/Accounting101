import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { HlmSelectImports } from '@spartan-ng/helm/select';
import { PayablesService } from '../core/payables/payables.service';

/** The vendor picker shared by the Payables list tabs. Bound to the service's persisted
 *  per-client selection, so choosing a vendor on one tab carries to the others. */
@Component({
  selector: 'app-vendor-select',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [...HlmSelectImports],
  template: `
    <div hlmSelect [value]="svc.selectedVendorId()" [itemToString]="toName"
         (valueChange)="svc.setSelectedVendor($any($event) ?? '')">
      <hlm-select-trigger class="w-64">
        <hlm-select-value placeholder="Select a vendor" />
      </hlm-select-trigger>
      <hlm-select-content *hlmSelectPortal>
        @for (v of svc.vendors(); track v.id) {
          <hlm-select-item [value]="v.id">{{ v.name }}</hlm-select-item>
        }
      </hlm-select-content>
    </div>
  `,
})
export class VendorSelect {
  readonly svc = inject(PayablesService);
  readonly toName = (id: string): string => this.svc.vendorName(id);
}
