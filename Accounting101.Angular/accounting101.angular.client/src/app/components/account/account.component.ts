import {Component, inject, OnDestroy, OnInit, output} from '@angular/core';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {AccountHeaderComponent} from '../../controls/account-header/account-header.component';
import {MenuComponent} from '../menu/menu.component';
import {Screen} from '../../enums/screen.enum';
import {TransactionListComponent} from '../../controls/transaction-list/transaction-list.component';
import {ClientHeaderComponent} from '../../controls/client-header/client-header.component';
import {ClientClient} from '../../clients/client-client/client-client.service';
import {AsyncPipe} from '@angular/common';
import {MatDivider} from '@angular/material/divider';
import {FastEntryComponent} from '../../controls/fast-entry/fast-entry.component';
import {AccountsClient} from '../../clients/accounts-client/accounts-client.service';
import {AccountModel} from '../../models/account.model';
import {RefreshService} from '../../services/refresh/refresh.service';
import {Subscription} from 'rxjs';
import {TransactionDisplayLine} from '../../models/transaction-display-line.model.cs';
import {TransactionModel} from '../../models/transaction.model';

@Component({
  selector: 'app-account',
  imports: [
    AccountHeaderComponent,
    MenuComponent,
    TransactionListComponent,
    ClientHeaderComponent,
    AsyncPipe,
    MatDivider,
    FastEntryComponent
  ],
  templateUrl: './account.component.html',
  styleUrl: './account.component.scss'
})

export class AccountComponent implements OnInit, OnDestroy{
  private refreshSubscription!: Subscription;
  account: AccountModel = new AccountModel();
  private readonly refreshService = inject(RefreshService)
  private readonly globals = inject(GlobalConstantsService);
  private readonly userDataService = inject(UserDataService);
  private readonly clientManager = inject(ClientClient);
  private readonly accountsManager = inject(AccountsClient);
  readonly client = this.clientManager.getClient(this.userDataService.get(this.globals.clientIdKey));
  readonly accounts = this.accountsManager.getAccounts();
  filteredAccounts: AccountModel[] = [];
  allAccounts: AccountModel[] = [];

  protected readonly Screen = Screen;

  constructor() {
    this.initialize();
  }

  ngOnInit() {
    this.refreshSubscription = this.refreshService.refresh$.subscribe(() => {
      this.initialize();
    });
  }

  initialize() {
    const accountId = this.userDataService.get(this.globals.accountIdKey);
    this.accounts.subscribe(accts => {
      this.allAccounts = accts;
      this.account = accts.find(a => a.id === accountId)!;
      this.filteredAccounts = accts.filter(a => a.id !== accountId);
    });
  }

  linkClicked(destAccount: string) {
    const destination = this.allAccounts.find(a => a.info.coAId + ' ' + a.info.name === destAccount)!;
    this.userDataService.set(this.globals.accountIdKey, destination.id);
    this.refreshService.refresh();
  }

  ngOnDestroy() {
    if (this.refreshSubscription) {
      this.refreshSubscription.unsubscribe();
    }
  }
}
