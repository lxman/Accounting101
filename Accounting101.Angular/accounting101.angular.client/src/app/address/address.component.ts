import { Component } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormControl, FormGroup, Validators, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { Address } from '../../../Models/address.interface';
import { UsAddressModel } from '../../../Models/us-address.model';
import { ForeignAddressModel } from '../../../Models/foreign-address.model';
import { AddressManagerService } from '../../services/address-manager/address-manager.service';
import { MatOptionModule } from '@angular/material/core';
import { MatSelectChange, MatSelectModule } from '@angular/material/select';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { NgIf } from '@angular/common';
import { MatButton } from '@angular/material/button';
import { SelectComponent } from '../controls/select/select.component';

@Component({
    selector: 'app-address',
    templateUrl: './address.component.html',
    styleUrl: './address.component.scss',
    imports: [FormsModule, ReactiveFormsModule, MatFormField, MatLabel, MatInput, NgIf, MatButton, MatOptionModule, MatSelectModule, CommonModule, SelectComponent]
})

export class AddressComponent {
  addressForm = new FormGroup({
    isForeign: new FormControl(false),
    line1: new FormControl('', Validators.required),
    line2: new FormControl(''),
    cityProvince: new FormControl('', Validators.required),
    state: new FormControl('', Validators.required),
    zipPostCode: new FormControl('', Validators.required),
    country: new FormControl('', Validators.required)
  });
  states: any[] = [];
  countries: any[] = [];

  constructor(private readonly addressManager: AddressManagerService) {
    this.addressManager.getStates().subscribe(states => this.states = states);
    this.addressManager.getCountries().subscribe(countries => this.countries = countries);
  }

  stateSelected(event: MatSelectChange<any>): void {
    event.value && this.addressForm.patchValue({ state: event.value });
  }

  countrySelected(country: string): void {
    this.addressForm.patchValue({ country });
  }

  toggleAddressForm(): void {
    this.addressForm.patchValue({ isForeign: !this.addressForm.value.isForeign });
  }

  onSubmit(): void {
    const address: Address = this.addressForm.value.isForeign
      ? new ForeignAddressModel(
        this.addressForm.value.line1!,
        this.addressForm.value.line2!,
        this.addressForm.value.cityProvince!,
        this.addressForm.value.zipPostCode!,
        this.addressForm.value.country!
      )
      : new UsAddressModel(
        this.addressForm.value.line1!,
        this.addressForm.value.line2!,
        this.addressForm.value.cityProvince!,
        this.addressForm.value.state!,
        this.addressForm.value.zipPostCode!
      );

    console.log(address);
  }
}
