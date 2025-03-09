import { Component } from '@angular/core';
import { BusinessModel } from '../../../Models/business.model';
import { BusinessManagerService } from '../../services/business-manager/business-manager.service';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldControl, MatFormFieldModule } from '@angular/material/form-field';
import { BrowserModule } from '@angular/platform-browser';
import { AppRoutingModule } from '../app-routing.module';
import { MatInputModule } from '@angular/material/input';
import { AddressComponent } from '../address/address.component';
import { AddressManagerService } from '../../services/address-manager/address-manager.service';

@Component({
    selector: 'app-create-business',
    templateUrl: './create-business.component.html',
    styleUrl: './create-business.component.scss',
    providers: [{ provide: MatFormFieldControl, useExisting: CreateBusinessComponent }],
    imports: [
    BrowserModule,
    AppRoutingModule,
    FormsModule,
    ReactiveFormsModule,
    MatFormFieldModule,
    MatInputModule,
    AddressComponent
]
})

export class CreateBusinessComponent {
  createBusinessForm = new FormGroup({
    name: new FormControl('', Validators.required)
  });
  addressForm: AddressComponent;

  constructor(
    addressService: AddressManagerService,
    businessService: BusinessManagerService,
    AddressComponent: AddressComponent) {
      this.addressForm = AddressComponent;
  }

  onSubmit() {
    if (this.createBusinessForm.valid) {
      const business = new BusinessModel();
      business.name = this.createBusinessForm.value.name ?? '';
      business.address = this.addressForm.value;
    }
  }
}
