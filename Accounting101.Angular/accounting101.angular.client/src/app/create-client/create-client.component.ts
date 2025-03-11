import { Component, forwardRef } from '@angular/core';
import { FormControl, FormGroup, FormsModule, ReactiveFormsModule, Validators } from '@angular/forms';
import { MatFormFieldControl, MatFormFieldModule } from '@angular/material/form-field';
import { MatInputModule } from '@angular/material/input';
import { MatButtonModule } from '@angular/material/button';
import { ClientModel } from '../../../Models/client.model';
import { PersonNameComponent } from "../controls/person-name/person-name.component";
import { PersonNameModel } from '../../../Models/person-name.model';
import { AddressComponent } from "../address/address.component";
import { AddressManagerService } from '../../services/address-manager/address-manager.service';
import { PersonNameManagerService } from '../../services/person-name/person-name-manager.service';
import { ClientManagerService } from '../../services/client-manager/client-manager.service';
import { Router } from '@angular/router';
import { AddressModel } from '../../../Models/address.model';
import { CreateClientModel } from '../../../Models/create-client.model';
import { switchMap, tap } from 'rxjs';

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

  router: Router = new Router();

  states: any[] = [];
  countries: any[] = [];

  constructor(
    private readonly addressService: AddressManagerService,
    private readonly personNameService: PersonNameManagerService,
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

  onSubmit() {
    const businessName = this.createClientForm.get('businessName')?.value;
    const contactGroup = this.createClientForm.get('contactGroup');
    const addressGroup = this.createClientForm.get('addressGroup');
    if (contactGroup == null || addressGroup == null) {
      console.log('Contact or address group is null');
      return;
    }
    const contact = new PersonNameModel();
    contact.prefix = contactGroup.get('prefix')?.value ?? '';
    contact.first = contactGroup.get('first')?.value ?? '';
    contact.middle = contactGroup.get('middle')?.value ?? '';
    contact.last = contactGroup.get('last')?.value ?? '';
    contact.suffix = contactGroup.get('suffix')?.value ?? '';
    const address = new AddressModel();
    address.isForeign = addressGroup.get('isForeign')?.value ?? false;
    address.line1 = addressGroup.get('line1')?.value ?? '';
    address.line2 = addressGroup.get('line2')?.value ?? '';
    address.city = addressGroup.get('cityProvince')?.value ?? '';
    address.stateProvince = addressGroup.get('state')?.value ?? '';
    address.postalCode = addressGroup.get('zipPostCode')?.value ?? '';
    address.country = addressGroup.get('country')?.value ?? '';
    const client = new ClientModel(businessName ?? '', contact, address);
    this.personNameService.createPersonName(contact).pipe(
      tap((personName) => client.contactName.id = personName.id),
      switchMap(() => this.addressService.createAddress(address)),
      tap((addressId) => client.address.id = addressId),
      switchMap(() => this.clientService.createClient(new CreateClientModel(client.businessName, client.contactName.id, client.address.id)))
    ).subscribe({
      next: () => {
        this.router.navigate(['/client-selector']);
      },
      error: (error) => {
        console.error(error);
      }
    });
  }
}
