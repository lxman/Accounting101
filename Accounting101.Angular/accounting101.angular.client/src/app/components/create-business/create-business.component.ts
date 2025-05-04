import { Component, forwardRef } from '@angular/core';
import { BusinessModel } from '../../models/business.model';
import { BusinessClient } from '../../clients/business-client/business-client.service';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldControl, MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AddressComponent } from '../../controls/address/address.component';
import { AddressClient } from '../../clients/address-client/address-client.service';
import { ForeignAddressModel } from '../../models/foreign-address.model';
import { UsAddressModel } from '../../models/us-address.model';
import { MatButtonModule } from '@angular/material/button';
import { Router } from '@angular/router';

@Component({
    selector: 'app-create-business',
    templateUrl: './create-business.component.html',
    styleUrl: './create-business.component.scss',
    providers: [{ provide: MatFormFieldControl, useExisting: CreateBusinessComponent }],
    imports: [
    FormsModule,
    ReactiveFormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    forwardRef(() => AddressComponent)]
})

export class CreateBusinessComponent {
  createBusinessForm = new FormGroup({
    name: new FormControl('', Validators.required),
    addressGroup: new FormGroup({
      isForeign: new FormControl(false),
      line1: new FormControl('', Validators.required),
      line2: new FormControl(''),
      cityProvince: new FormControl('', Validators.required),
      state: new FormControl(''),
      zipPostCode: new FormControl('', Validators.required),
      country: new FormControl('')
    },
    { validators: [Validators.required] })
  });

  states: any[] = [];
  countries: any[] = [];

  constructor(
    private readonly addressService: AddressClient,
    private readonly businessService: BusinessClient,
    private readonly router: Router) {
      addressService.getStates().subscribe(states => this.states = states);
      addressService.getCountries().subscribe(countries => this.countries = countries);
  }

  handleToggled() {
    const addressGroup = this.createBusinessForm.get('addressGroup');
    if (addressGroup == null) {
            return;
    }
    addressGroup.get('state')?.setValue('');
    addressGroup.get('country')?.setValue('');
  }

  onSubmit() {
    if (this.createBusinessForm.valid) {
      const business = new BusinessModel();
      business.name = this.createBusinessForm.value.name ?? '';
      const addressGroup = this.createBusinessForm.value.addressGroup;
      if (addressGroup == null) {
                return;
      }
      if (addressGroup.isForeign) {
        business.address = {
          id: '',
          isForeign: addressGroup.isForeign,
          line1: addressGroup.line1 ?? '',
          line2: addressGroup.line2 ?? '',
          cityProvince: addressGroup.cityProvince ?? '',
          province: addressGroup.cityProvince ?? '',
          postCode: addressGroup.zipPostCode ?? '',
          country: addressGroup.country ?? '',
          asString: ''
        } as ForeignAddressModel;
      } else {
        business.address = {
          isForeign: addressGroup.isForeign,
          line1: addressGroup.line1 ?? '',
          line2: addressGroup.line2 ?? '',
          city: addressGroup.cityProvince ?? '',
          state: addressGroup.state ?? '',
          zip: addressGroup.zipPostCode ?? '',
          asString: ''
        } as UsAddressModel;
      }
      this.businessService.createBusiness(business).subscribe({
        complete: () => {
          void this.router.navigate(['/create-client']);
        },
        error: (error) => {
          console.error(error);
        }
      });
      return;
    }
      }
}
