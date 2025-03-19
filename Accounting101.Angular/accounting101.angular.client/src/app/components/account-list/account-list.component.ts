import {Component, inject} from '@angular/core';
import {AccountOrganizerComponent} from '../../controls/account-organizer/account-organizer.component';
import {AccountsManagerService} from '../../services/accounts-manager/accounts-manager.service';
import {AccountModel} from '../../models/account.model';
import {RootGroups} from '../../models/root-groups.model';
import {AccountGroupModel} from '../../models/account-group.model';
import {BaseAccountType} from '../../enums/base-account-types.enum';

@Component({
  selector: 'app-account-list',
  imports: [
    AccountOrganizerComponent
  ],
  templateUrl: './account-list.component.html',
  styleUrl: './account-list.component.scss'
})
export class AccountListComponent{
  accountService: AccountsManagerService = inject(AccountsManagerService);
  accounts: AccountModel[] = [];
  layouts: RootGroups = new RootGroups();
  assetAccounts: AccountModel[] = [];
  liabilityAccounts: AccountModel[] = [];
  equityAccounts: AccountModel[] = [];
  revenueAccounts: AccountModel[] = [];
  expenseAccounts: AccountModel[] = [];
  earningsAccounts: AccountModel[] = [];
  assetLayout: AccountGroupModel = new AccountGroupModel('Assets');
  liabilityLayout: AccountGroupModel = new AccountGroupModel('Liabilities');
  equityLayout: AccountGroupModel = new AccountGroupModel('Equity');
  revenueLayout: AccountGroupModel = new AccountGroupModel('Revenue');
  expenseLayout: AccountGroupModel = new AccountGroupModel('Expenses');
  earningsLayout: AccountGroupModel = new AccountGroupModel('Earnings');

  constructor() {
    this.accountService.getAccounts().subscribe({
      next: (accounts) => {
        this.accounts = accounts;
        this.accountService.getLayout().subscribe({
          next: (layout) => {
            this.layouts = layout;
            this.assetAccounts = this.accounts.filter(a => a.type === BaseAccountType.asset);
            this.liabilityAccounts = this.accounts.filter(a => a.type === BaseAccountType.liability);
            this.equityAccounts = this.accounts.filter(a => a.type === BaseAccountType.equity);
            this.revenueAccounts = this.accounts.filter(a => a.type === BaseAccountType.revenue);
            this.expenseAccounts = this.accounts.filter(a => a.type === BaseAccountType.expense);
            this.earningsAccounts = this.accounts.filter(a => a.type === BaseAccountType.earnings);
            this.assetLayout = this.layouts.assets;
            this.liabilityLayout = this.layouts.liabilities;
            this.equityLayout = this.layouts.equity;
            this.revenueLayout = this.layouts.revenue;
            this.expenseLayout = this.layouts.expenses;
            this.earningsLayout = this.layouts.earnings;
          },
          error: (error) => {
            console.error(error);
          }
        })
      },
      error: (error) => {
        console.error(error);
      }
    });
  }
}
