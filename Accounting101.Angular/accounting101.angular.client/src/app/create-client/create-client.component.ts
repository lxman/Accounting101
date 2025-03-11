import { Component, forwardRef } from '@angular/core';
import { ClientManagerService } from '../../services/client-manager/client-manager.service';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldControl, MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { ClientModel } from '../../../Models/client.model';
import { PersonNameComponent } from "../controls/person-name/person-name.component";
import { PersonNameModel } from '../../../Models/person-name.model';
import { AddressComponent } from "../address/address.component";
import { AddressManagerService } from '../../services/address-manager/address-manager.service';
import { Router } from '@angular/router';

@Component({
  selector: 'app-create-client',
  templateUrl: './create-client.component.html',
  styleUrl: './create-client.component.scss',
  imports: [
    ReactiveFormsModule,
    FormsModule,
    MatFormFieldModule,
    MatInputModule,
    MatButtonModule,
    forwardRef(() => AddressComponent),
    forwardRef(() => PersonNameComponent)],
  providers: [{ provide: MatFormFieldControl, useExisting: CreateClientComponent }]
})

export class CreateClientComponent {
  createClientForm = new FormGroup({
    businessName: new FormControl('', Validators.required),
    contactGroup: new FormGroup({
      prefix: new FormControl(''),
      first: new FormControl('', Validators.required),
      middle: new FormControl(''),
      last: new FormControl('', Validators.required),
      suffix: new FormControl('')
    }),
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
    private readonly clientService: ClientManagerService) {
    addressService.getStates().subscribe((states) => this.states = states);
    addressService.getCountries().subscribe((countries) => this.countries = countries);
  }

  handleToggled() {
    const addressGroup = this.createClientForm.get('addressGroup');
    if (addressGroup == null) {
      console.log('Address group is null');
      return;
    }
    addressGroup.get('state')?.setValue('');
    addressGroup.get('country')?.setValue('');
  }
}
