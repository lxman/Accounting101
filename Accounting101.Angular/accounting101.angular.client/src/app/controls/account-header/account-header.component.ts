import {Component, inject, input, OnChanges, SimpleChanges} from '@angular/core';
import {AccountsManagerService} from '../../services/accounts-manager/accounts-manager.service';
import {AccountModel} from '../../models/account.model';

@Component({
  selector: 'app-account-header',
  imports: [],
  templateUrl: './account-header.component.html',
  styleUrl: './account-header.component.scss'
})

export class AccountHeaderComponent implements OnChanges{
  readonly accountId = input.required<string>();
  private readonly accounts: AccountsManagerService = inject(AccountsManagerService);
  private account: AccountModel = new AccountModel();

  ngOnChanges(changes:SimpleChanges) {
    if (changes['accountId']) {
      this.accounts.getAccounts().subscribe((accounts) => {
        this.account = accounts.find(a => a.id === this.accountId())!;
        console.log('Found the account for id ' + this.account.id);
      });
    }
  }
}
