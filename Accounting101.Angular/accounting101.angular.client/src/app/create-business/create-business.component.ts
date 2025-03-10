import { Component, forwardRef } from '@angular/core';
import { BusinessModel } from '../../../Models/business.model';
import { BusinessManagerService } from '../../services/business-manager/business-manager.service';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule, RequiredValidator, Validators } from '@angular/forms';
import { MatFormFieldControl, MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { AddressComponent } from '../address/address.component';
import { AddressManagerService } from '../../services/address-manager/address-manager.service';
import { ForeignAddressModel } from '../../../Models/foreign-address.model';
import { UsAddressModel } from '../../../Models/us-address.model';

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
    private readonly addressService: AddressManagerService,
    private readonly businessService: BusinessManagerService) {
      addressService.getStates().subscribe(states => this.states = states);
      addressService.getCountries().subscribe(countries => this.countries = countries);
  }

  setState(state: string) {
    this.createBusinessForm.get('addressGroup.state')?.setValue(state);
  }

  setCountry(country: string) {
    this.createBusinessForm.get('addressGroup.country')?.setValue(country);
  }

  handleToggled() {
    const addressGroup = this.createBusinessForm.get('addressGroup');
    if (addressGroup == null) {
      console.log('Address group is null');
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
        console.log('Address group is null');
        return;
      }
      if (addressGroup.isForeign) {
        business.address = {
          isForeign: addressGroup.isForeign,
          line1: addressGroup.line1 ?? '',
          line2: addressGroup.line2 ?? '',
          cityProvince: addressGroup.cityProvince ?? '',
          province: addressGroup.cityProvince ?? '',
          postCode: addressGroup.zipPostCode ?? '',
          country: addressGroup.country ?? ''
        } as ForeignAddressModel;
      } else {
        business.address = {
          isForeign: addressGroup.isForeign,
          line1: addressGroup.line1 ?? '',
          line2: addressGroup.line2 ?? '',
          city: addressGroup.cityProvince ?? '',
          state: addressGroup.state ?? '',
          zip: addressGroup.zipPostCode ?? ''
        } as UsAddressModel;
      }
      this.businessService.createBusiness(business).subscribe(() => {
        console.log('Business created successfully');
      });
      return;
    }
    console.log('Form is invalid');
  }
}
