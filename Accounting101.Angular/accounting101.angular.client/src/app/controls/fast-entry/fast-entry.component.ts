import {Component, inject, input, OnChanges, OnDestroy, SimpleChanges} from '@angular/core';
import {MatNativeDateModule, MatOption} from '@angular/material/core';
import {MatFormField, MatHint, MatLabel} from '@angular/material/form-field';
import {FormControl, FormGroup, ReactiveFormsModule, Validators} from '@angular/forms';
import {MatInput} from '@angular/material/input';
import {MatButton} from '@angular/material/button';
import {MatDatepicker, MatDatepickerInput, MatDatepickerToggle} from '@angular/material/datepicker';
import {MatRadioButton, MatRadioGroup, MatRadioModule} from '@angular/material/radio';
import {AccountModel} from '../../models/account.model';
import {TransactionModel} from '../../models/transaction.model';
import {AccountsClient} from '../../clients/accounts-client/accounts-client.service';
import {MessageService} from '../../services/message/message.service';
import {Message} from '../../models/message.model';
import {NgForOf} from '@angular/common';
import {MatSelect} from '@angular/material/select';

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
    MatSelect,
    MatOption,
    NgForOf,
  ],
  templateUrl: './fast-entry.component.html',
  styleUrl: './fast-entry.component.scss'
})

export class FastEntryComponent implements OnChanges, OnDestroy{
  private readonly accountService = inject(AccountsClient);
  private readonly messageService = inject(MessageService);
  accountId = input.required<string>();
  accounts = input.required<AccountModel[]>();
  editRow = input<TransactionModel>();
  private transactionId = '';
  buttonTitle = 'Create';
  readonly initialDate: Date = new Date();
  accts: string[] = [];
  txSubscription = this.messageService.message$.subscribe((message: Message<TransactionModel>) => {
    if (message.type === 'TransactionModel' && message.destination === 'app-fast-entry') {
      this.buttonTitle = 'Update';
      const transaction = message.message;
      this.transactionId = transaction.id;
      this.fastEntryGroup.patchValue({
        date: new Date(transaction.when + 'T00:00:00'),
        creditDebit: transaction.creditedAccountId === this.accountId() ? 'credit' : 'debit',
        amount: transaction.amount,
        otherAccount: this.accounts().find(acct => acct.id === (transaction.creditedAccountId === this.accountId() ? transaction.debitedAccountId : transaction.creditedAccountId))?.info.name
      });
    }
  });
  editingSubscription = this.messageService.message$.subscribe((message: Message<boolean>) => {
    if (message.type === 'boolean' && message.destination === 'app-fast-entry') {
      if (message.message) {
        this.updateTransaction();
      } else if (!message.message) {
        this.newTransaction();
      }
    }
  });
  clearSubscription = this.messageService.message$.subscribe((message: Message<string>) => {
    if (message.type === 'string' && message.destination === 'app-fast-entry' && message.message === 'clear') {
      this.buttonTitle = 'Create';
      this.fastEntryGroup.reset();
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
            this.accts = this.accounts().map(acct => acct.info.name);

      // Force the component to update
      setTimeout(() => {
        // This will trigger Angular's change detection
      }, 0);
    }
  }

  onSubmit() {
    if (!this.fastEntryGroup.valid) {
      console.log('Form validation failed:', this.fastEntryGroup.errors);
      console.log('Form values:', this.fastEntryGroup.value);
      console.log('Available accounts:', this.accts);
      return;
    }

    console.log('Form submitted successfully with values:', this.fastEntryGroup.value);

    const message = new Message<string>();
    message.source = 'app-fast-entry';
    message.destination = 'app-transaction-list';
    message.message = 'editing?';
    message.type = 'string';
    this.messageService.sendMessage(message);
  }

  newTransaction() {
    let tx = new TransactionModel();
    tx = this.buildTransaction(tx);

    console.log('Creating new transaction:', tx);

    // Check if transaction is valid before sending
    if (!tx.creditedAccountId || !tx.debitedAccountId) {
      console.error('Invalid transaction: missing account IDs', tx);
      return;
    }

    this.accountService.createTransaction(tx).subscribe({
      next: (result) => {
        console.log('Transaction created successfully, result:', result);
        this.updateTransactionList();
      },
      error: (error) => {
        console.error('Error creating transaction:', error);
      }
    });
  }

  updateTransaction() {
    let tx = new TransactionModel();
    tx.id = this.transactionId;
    tx = this.buildTransaction(tx);
    this.accountService.updateTransaction(tx).subscribe(() => {
      this.updateTransactionList();
    })
  }

  buildTransaction(tx: TransactionModel) {
    const formValues = this.fastEntryGroup.value;

    tx.when = formValues.date?.toISOString().split("T")[0] ?? '';
    tx.amount = formValues.amount as number;

    // Get the selected other account name
    const otherAccountName = formValues.otherAccount as string;
    console.log('Building transaction with other account name:', otherAccountName);

    // Find the account object by name
    const otherAccount = this.accounts().find(acct => acct.info.name === otherAccountName);
    console.log('Found other account:', otherAccount);

    if (!otherAccount) {
      console.error('Could not find other account with name:', otherAccountName);
      console.log('Available accounts:', this.accounts());
      return tx;
    }

    const otherAccountId = otherAccount.id;

    if (formValues.creditDebit === 'credit') {
      tx.creditedAccountId = this.accountId();
      tx.debitedAccountId = otherAccountId;
    } else {
      tx.creditedAccountId = otherAccountId;
      tx.debitedAccountId = this.accountId();
    }

    console.log('Built transaction:', tx);
    return tx;
  }

  updateTransactionList() {
    const message = new Message<string>();
    message.source = 'app-fast-entry';
    message.destination = 'app-transaction-list';
    message.message = 'update';
    message.type = 'string';
    this.messageService.sendMessage(message);
  }

  ngOnDestroy() {
    this.txSubscription.unsubscribe();
    this.editingSubscription.unsubscribe();
    this.clearSubscription.unsubscribe();
  }
}
