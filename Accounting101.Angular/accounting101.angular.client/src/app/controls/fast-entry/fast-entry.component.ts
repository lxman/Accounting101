import {Component, input, OnChanges, SimpleChanges} from '@angular/core';
import {MatNativeDateModule} from '@angular/material/core';
import {MatFormField} from '@angular/material/form-field';
import {FormControl, FormGroup, ReactiveFormsModule, Validators} from '@angular/forms';
import {MatInput} from '@angular/material/input';
import {MatLabel} from '@angular/material/form-field';
import {MatButton} from '@angular/material/button';
import {MatHint} from '@angular/material/form-field';
import {MatDatepicker, MatDatepickerInput, MatDatepickerToggle} from '@angular/material/datepicker';
import {MatRadioButton, MatRadioGroup, MatRadioModule} from '@angular/material/radio';
import {AccountModel} from '../../models/account.model';
import {SelectComponent} from '../select/select.component';

@Component({
  selector: 'app-fast-entry',
  imports: [
    MatFormField,
    ReactiveFormsModule,
    MatInput,
    MatLabel,
    MatButton,
    MatDatepickerInput,
    MatDatepickerToggle,
    MatDatepicker,
    MatHint,
    MatNativeDateModule,
    MatRadioGroup,
    MatRadioButton,
    MatRadioModule,
    SelectComponent
  ],
  templateUrl: './fast-entry.component.html',
  styleUrl: './fast-entry.component.scss'
})

export class FastEntryComponent implements OnChanges{
  accounts = input.required<AccountModel[]>();
  readonly initialDate: Date = new Date();
  accts: string[] = [];

  readonly fastEntryGroup = new FormGroup({
    date: new FormControl(this.initialDate, Validators.required),
    creditDebit: new FormControl('', Validators.required),
    amount: new FormControl<number>(0, Validators.required),
    otherAccount: new FormControl('', Validators.required)
  });

  ngOnChanges(changes: SimpleChanges) {
    if (changes['accounts']) {
      this.accts = this.accounts().map(acct => acct.info.name)
    }
  }
}
