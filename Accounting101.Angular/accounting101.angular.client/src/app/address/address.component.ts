import { Component, EventEmitter, inject, Input, OnInit, Output } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormControl, FormGroup, Validators, FormsModule, ReactiveFormsModule, ControlContainer, FormGroupDirective } from '@angular/forms';
import { UsAddressModel } from '../../../Models/us-address.model';
import { ForeignAddressModel } from '../../../Models/foreign-address.model';
import { MatOptionModule } from '@angular/material/core';
import { MatSelect, MatSelectChange, MatSelectModule } from '@angular/material/select';
import { MatFormField, MatFormFieldControl, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { NgIf } from '@angular/common';
import { MatButton } from '@angular/material/button';
import { SelectComponent } from '../controls/select/select.component';

@Component({
    selector: 'address-component',
    templateUrl: './address.component.html',
    styleUrl: './address.component.scss',
    imports: [
      FormsModule,
      ReactiveFormsModule,
      MatFormField,
      MatLabel,
      MatInput,
      NgIf,
      MatButton,
      MatOptionModule,
      MatSelectModule,
      CommonModule,
      SelectComponent],
    providers: [{ provide: MatFormFieldControl, useExisting: AddressComponent }],
    viewProviders: [
      {
        provide: ControlContainer,
        useFactory: () => inject(ControlContainer, {skipSelf: true})
      }
    ]
})

export class AddressComponent implements OnInit {
  @Input() states: any[] = [];
  @Input() countries: any[] = [];
  @Output() toggledEvent = new EventEmitter<void>();

  stateRequired: boolean = false;
  countryRequired: boolean = false;

  addressForm = new FormGroup({
    isForeign: new FormControl(false),
    line1: new FormControl('', Validators.required),
    line2: new FormControl(''),
    cityProvince: new FormControl('', Validators.required),
    state: new FormControl('', Validators.required),
    zipPostCode: new FormControl('', Validators.required),
    country: new FormControl('', Validators.required)
  });

  ngOnInit(): void {
    this.addressForm = this.rootFormGroup.control.get('addressGroup') as FormGroup;
    this.addressForm.valueChanges.subscribe(() => {
      this.stateRequired = !this.addressForm.value.isForeign!;
      this.countryRequired = this.addressForm.value.isForeign!;
    });
  }

  constructor(private rootFormGroup: FormGroupDirective) {}

  toggleAddressForm(): void {
    this.addressForm.patchValue({ isForeign: !this.addressForm.value.isForeign });
    this.toggledEvent.emit();
  }

  public getValue(): UsAddressModel | ForeignAddressModel {
    return this.addressForm.value.isForeign
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
  }
}
