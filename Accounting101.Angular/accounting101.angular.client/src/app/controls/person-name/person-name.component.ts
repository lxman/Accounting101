import { Component, inject, Input, OnDestroy, OnInit } from '@angular/core';
import { PersonNameModel } from '../../../../Models/person-name.model';
import { MatFormFieldControl, MatFormField, MatLabel } from '@angular/material/form-field';
import { Subject } from 'rxjs';
import { NgControl, AbstractControlDirective, FormGroup, ControlValueAccessor, FormGroupDirective, ControlContainer } from '@angular/forms';
import { ReactiveFormsModule } from '@angular/forms';
import { MatInputModule } from '@angular/material/input';

@Component({
  selector: 'person-name-component',
  templateUrl: './person-name.component.html',
  styleUrl: './person-name.component.scss',
  imports: [
    ReactiveFormsModule,
    MatFormField,
    MatLabel,
    MatInputModule],
  providers: [{ provide: MatFormFieldControl, useExisting: PersonNameComponent }],
  viewProviders: [
    {
      provide: ControlContainer,
      useFactory: () => inject(ControlContainer, {skipSelf: true})
    }
  ]
})
export class PersonNameComponent implements MatFormFieldControl<PersonNameModel>, ControlValueAccessor, OnDestroy, OnInit {
  @Input() show: boolean = false;
  @Input() required: boolean = false;
  @Input() groupName: string = '';

  personNameForm: FormGroup = new FormGroup({});

  static nextId = 0;
  ngControl: NgControl | AbstractControlDirective | null = null;
  stateChanges = new Subject<void>();
  id = `person-name-component-${PersonNameComponent.nextId++}`;
  placeholder: string = '';
  focused: boolean = false;
  empty: boolean = true;
  shouldLabelFloat: boolean = false;
  disabled: boolean = false;
  errorState: boolean = false;
  controlType?: string | undefined = 'person-name-component';
  autofilled?: boolean | undefined;
  userAriaDescribedBy?: string | undefined;
  disableAutomaticLabeling?: boolean | undefined;

  ngOnInit(): void {
    this.personNameForm = this.rootFormGroup.control.get(this.groupName) as FormGroup;
  }

  private _value: PersonNameModel = new PersonNameModel();

  set value(val: PersonNameModel) {
    this._value = val;
    this.stateChanges.next();
  }

  get value(): PersonNameModel {
    return this._value;
  }

  constructor(private rootFormGroup: FormGroupDirective) {}

  writeValue(obj: PersonNameModel): void {
    if (obj) {
      this.value = obj;
      this.personNameForm.setValue(obj, { emitEvent: false });
    }
  }

  registerOnChange(fn: any): void {
    this.personNameForm.valueChanges.subscribe(fn);
  }

  registerOnTouched(fn: any): void {
    this.onTouched = fn;
  }

  setDisabledState(isDisabled: boolean): void {
    if (isDisabled) {
      this.personNameForm.disable();
    } else {
      this.personNameForm.enable();
    }
    this.disabled = isDisabled;
    this.stateChanges.next();
  }

  setDescribedByIds(ids: string[]): void {
    this.userAriaDescribedBy = ids.join(' ');
  }

  onContainerClick(event: MouseEvent): void {
    if ((event.target as Element).tagName.toLowerCase() !== 'input') {
      this.personNameForm.get('firstName')?.markAsTouched();
    }
  }

  onTouched: () => void = () => {};

  ngOnDestroy(): void {
    this.stateChanges.complete();
  }
}
