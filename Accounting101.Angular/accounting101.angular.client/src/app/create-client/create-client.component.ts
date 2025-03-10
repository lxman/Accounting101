import { Component } from '@angular/core';
import { ClientModel } from '../../../Models/client.model';
import { PersonNameModel } from '../../../Models/person-name.model';
import { FormControl, FormGroup, Validators } from '@angular/forms';

@Component({
  selector: 'app-create-client',
  standalone: false,
  templateUrl: './create-client.component.html',
  styleUrl: './create-client.component.scss'
})
export class CreateClientComponent {
  createClientForm = new FormGroup({
    businessName: new FormControl('', Validators.required),
    personNameGroup: new FormGroup({
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
}
