import { CommonModule } from '@angular/common';
import { Component, inject, Input, OnDestroy, Optional, Self } from '@angular/core';
import { MatOptionModule } from '@angular/material/core';
import { MatSelectModule } from '@angular/material/select';
import { MatFormFieldControl } from '@angular/material/form-field';
import { ControlContainer, NgControl, ReactiveFormsModule, ControlValueAccessor } from '@angular/forms';
import { Subject } from 'rxjs';

@Component({
  selector: 'select-component',
  templateUrl: './select.component.html',
  styleUrls: ['./select.component.scss'],
  imports: [MatOptionModule, MatSelectModule, ReactiveFormsModule, CommonModule],
  providers: [{ provide: MatFormFieldControl, useExisting: SelectComponent }],
  viewProviders: [
    {
      provide: ControlContainer,
      useFactory: () => inject(ControlContainer, {skipSelf: true})
    }
  ]
})

export class SelectComponent implements MatFormFieldControl<string>, ControlValueAccessor, OnDestroy {
  @Input() items: any[] = [];
  @Input() show: boolean = false;
  @Input() required: boolean = false;

  static nextId = 0;
  stateChanges = new Subject<void>();
  id = `select-component-${SelectComponent.nextId++}`;
  placeholder: string = '';
  focused: boolean = false;
  empty: boolean = true;
  shouldLabelFloat: boolean = false;
  disabled: boolean = false;
  errorState: boolean = false;
  controlType?: string | undefined = 'select-component';
  autofilled?: boolean | undefined;
  userAriaDescribedBy?: string | undefined;
  disableAutomaticLabeling?: boolean | undefined;

  private _value: any;

  set value(val: any) {
    this._value = val;
    this.stateChanges.next();
  }

  get value(): any {
    return this._value;
  }

  writeValue(obj: any): void {
    this.value = obj;
  }

  constructor(@Optional() @Self() public ngControl: NgControl) {
    if (this.ngControl) {
      this.ngControl.valueAccessor = this;
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

  updateValue(event: any): void {
    this.value = event.target.value;
    this.onChange(this.value);
    this.onTouched();
  }

  onChange = (_: any) => {};
  onTouched = () => {};

  ngOnDestroy() {
    this.stateChanges.complete();
  }
}
