import {Component, inject, input, OnChanges, OnDestroy, output, SimpleChanges, ViewChild} from '@angular/core';
import {MatNativeDateModule} from '@angular/material/core';
import {MatFormField, MatHint, MatLabel} from '@angular/material/form-field';
import {FormControl, FormGroup, ReactiveFormsModule, Validators} from '@angular/forms';
import {MatInput} from '@angular/material/input';
import {MatButton} from '@angular/material/button';
import {MatDatepicker, MatDatepickerInput, MatDatepickerToggle} from '@angular/material/datepicker';
import {MatRadioButton, MatRadioGroup, MatRadioModule} from '@angular/material/radio';
import {AccountModel} from '../../models/account.model';
import {SelectComponent} from '../select/select.component';
import {TransactionModel} from '../../models/transaction.model';
import {AccountsClient} from '../../clients/accounts-client/accounts-client.service';
import {MessageService} from '../../services/message/message.service';
import {Message} from '../../models/message.model';

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

export class FastEntryComponent implements OnChanges, OnDestroy{
  private readonly accountService = inject(AccountsClient);
  private readonly messageService = inject(MessageService<TransactionModel>);
  accountId = input.required<string>();
  accounts = input.required<AccountModel[]>();
  editRow = input<TransactionModel>();
  transactionUpdated = output();
  readonly initialDate: Date = new Date();
  accts: string[] = [];
  @ViewChild("accountSelector") selector!: SelectComponent;
  subscription = this.messageService.message$.subscribe((message: Message<TransactionModel>) => {
    if (message.destination === 'app-fast-entry') {
      const transaction = message.message;
      this.fastEntryGroup.patchValue({
        date: new Date(transaction.when + 'T00:00:00'),
        creditDebit: transaction.creditedAccountId === this.accountId() ? 'credit' : 'debit',
        amount: transaction.amount,
        otherAccount: this.accounts().find(acct => acct.id === (transaction.creditedAccountId === this.accountId() ? transaction.debitedAccountId : transaction.creditedAccountId))?.info.name
      });
    }
  });

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

  onSubmit() {
    const tx = new TransactionModel();
    tx.when = this.fastEntryGroup.value.date?.toISOString().split("T")[0] ?? '';
    tx.amount = this.fastEntryGroup.value.amount as number;
    tx.creditedAccountId = this.fastEntryGroup.value.creditDebit === 'credit'
      ? this.accountId()
      : this.accounts().find(acct => acct.info.name === this.fastEntryGroup.value.otherAccount)?.id ?? '';
    tx.debitedAccountId = this.fastEntryGroup.value.creditDebit === 'debit'
      ? this.accountId()
      : this.accounts().find(acct => acct.info.name === this.fastEntryGroup.value.otherAccount)?.id ?? '';
    this.accountService.createTransaction(tx).subscribe(() => {
      this.transactionUpdated.emit();
    });
  }

  ngOnDestroy() {
    this.subscription.unsubscribe();
  }
}
