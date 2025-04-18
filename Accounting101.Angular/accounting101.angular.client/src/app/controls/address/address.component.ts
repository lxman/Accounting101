import { Component, EventEmitter, inject, OnInit, Output, input } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormGroup, FormsModule, ReactiveFormsModule, ControlContainer, FormGroupDirective } from '@angular/forms';
import { UsAddressModel } from '../../models/us-address.model';
import { ForeignAddressModel } from '../../models/foreign-address.model';
import { MatOptionModule } from '@angular/material/core';
import { MatSelectModule } from '@angular/material/select';
import { MatFormField, MatFormFieldControl, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { NgIf } from '@angular/common';
import { MatCheckboxModule } from '@angular/material/checkbox';

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
      MatCheckboxModule,
      MatOptionModule,
      MatSelectModule,
      CommonModule
    ],
    providers: [{ provide: MatFormFieldControl, useExisting: AddressComponent }],
    viewProviders: [
      {
        provide: ControlContainer,
        useFactory: () => inject(ControlContainer, {skipSelf: true})
      }
    ]
})

export class AddressComponent implements OnInit {
  readonly states = input<any[]>([]);
  readonly countries = input<any[]>([]);
  readonly groupName = input<string>('');
  @Output() toggledEvent = new EventEmitter<void>();

  addressForm: FormGroup = new FormGroup({});

  stateRequired: boolean = false;
  countryRequired: boolean = false;

  ngOnInit(): void {
    this.addressForm = this.rootFormGroup.control.get(this.groupName()) as FormGroup;
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
