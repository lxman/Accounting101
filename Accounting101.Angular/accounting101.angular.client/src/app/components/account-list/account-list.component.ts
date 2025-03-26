import {Component, inject} from '@angular/core';
import {AccountOrganizerComponent} from '../../controls/account-organizer/account-organizer.component';
import {AccountsManagerService} from '../../services/accounts-manager/accounts-manager.service';
import {AccountModel} from '../../models/account.model';
import {RootGroups} from '../../models/root-groups.model';
import {AccountGroupModel} from '../../models/account-group.model';
import {BaseAccountType} from '../../enums/base-account-type.enum';
import {MenuComponent} from '../menu/menu.component';
import {Screen} from '../../enums/screen.enum';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {ClientModel} from '../../models/client.model';
import {ClientManagerService} from '../../services/client-manager/client-manager.service';
import {ClientHeaderComponent} from '../../controls/client-header/client-header.component';
import {CdkScrollable} from '@angular/cdk/scrolling';

@Component({
  selector: 'app-account-list',
  imports: [
    AccountOrganizerComponent,
    MenuComponent,
    ClientHeaderComponent,
    CdkScrollable
  ],
  templateUrl: './account-list.component.html',
  styleUrl: './account-list.component.scss'
})

export class AccountListComponent{
  client: ClientModel | null = null;
  private readonly accountService: AccountsManagerService = inject(AccountsManagerService);
  private readonly clientService = inject(ClientManagerService);
  private readonly globals = inject(GlobalConstantsService);
  private readonly userData = inject(UserDataService);
  private readonly clientIdKey = this.userData.get(this.globals.clientIdKey);
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

  protected readonly Screen = Screen;

  constructor() {
    this.clientService.getClient(this.clientIdKey).subscribe({
      next: (client: ClientModel) => {
        this.client = client;
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
      },
      error: (error) => {
        console.error(error);
      }
    });
  }

  handleAssetLayoutChanged(layout: AccountGroupModel): void {
    this.layouts.assets = layout;
    this.saveLayout();
  }

  handleLiabilityLayoutChanged(layout: AccountGroupModel): void {
    this.layouts.liabilities = layout;
    this.saveLayout();
  }

  handleEquityLayoutChanged(layout: AccountGroupModel): void {
    this.layouts.equity = layout;
    this.saveLayout();
  }

  handleRevenueLayoutChanged(layout: AccountGroupModel): void {
    this.layouts.revenue = layout;
    this.saveLayout();
  }

  handleExpenseLayoutChanged(layout: AccountGroupModel): void {
    this.layouts.expenses = layout;
    this.saveLayout();
  }

  handleEarningsLayoutChanged(layout: AccountGroupModel): void {
    this.layouts.earnings = layout;
    this.saveLayout();
  }

  saveLayout(): void {
    this.accountService.saveLayout(this.layouts).subscribe({
      next: () => {},
      error: (error) => {
        console.error(error);
      }
    });
  }
}
