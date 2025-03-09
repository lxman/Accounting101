import { Component, inject, OnDestroy, Optional, Self } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormControl, FormGroup, Validators, FormsModule, ReactiveFormsModule, ControlValueAccessor, NgControl, ControlContainer } from '@angular/forms';
import { UsAddressModel } from '../../../Models/us-address.model';
import { ForeignAddressModel } from '../../../Models/foreign-address.model';
import { AddressManagerService } from '../../services/address-manager/address-manager.service';
import { MatOptionModule } from '@angular/material/core';
import { MatSelectChange, MatSelectModule } from '@angular/material/select';
import { MatFormField, MatFormFieldControl, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { NgIf } from '@angular/common';
import { MatButton } from '@angular/material/button';
import { SelectComponent } from '../controls/select/select.component';
import { Subject } from 'rxjs';

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

export class AddressComponent implements MatFormFieldControl<UsAddressModel | ForeignAddressModel>, ControlValueAccessor, OnDestroy {
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

  static nextId = 0;
  value: UsAddressModel | ForeignAddressModel | null = null;
  stateChanges = new Subject<void>();
  id: string = `address-component-${AddressComponent.nextId++}`;
  placeholder: string = '';
  focused: boolean = false;
  empty: boolean = true;
  shouldLabelFloat: boolean = false;
  required: boolean = false;
  disabled: boolean = false;
  errorState: boolean = false;
  controlType?: string | undefined;
  autofilled?: boolean | undefined;
  userAriaDescribedBy?: string | undefined;
  disableAutomaticLabeling?: boolean | undefined;

  constructor(
    private readonly addressManager: AddressManagerService,
    @Optional() @Self() public ngControl: NgControl) {
    if (this.ngControl) {
      this.ngControl.valueAccessor = this;
    }
    this.addressManager.getStates().subscribe(states => this.states = states);
    this.addressManager.getCountries().subscribe(countries => this.countries = countries);
  }

  onChange = (_: any) => {};
  onTouched = () => {};

  writeValue(obj: any): void {
    if (obj) {
      this.addressForm.patchValue(obj);
    }
  }

  registerOnChange(fn: any): void {
    this.onChange = fn;
  }

  registerOnTouched(fn: any): void {
    this.onTouched = fn;
  }

  setDisabledState?(isDisabled: boolean): void {
    this.disabled = isDisabled;
    this.stateChanges.next();
  }

  setDescribedByIds(ids: string[]): void {
    this.userAriaDescribedBy = ids.join(' ');
  }

  onContainerClick(event: MouseEvent): void {
    // Focus the select element when the container is clicked
    if ((event.target as Element).tagName.toLowerCase() !== 'select') {
      // Implement focus logic if needed
    }
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

  public getValue(): void {
    this.value = this.addressForm.value.isForeign
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

    console.log(this.value);
  }

  ngOnDestroy() {
    this.stateChanges.complete();
  }
}
