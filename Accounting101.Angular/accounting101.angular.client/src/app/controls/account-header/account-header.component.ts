import {Component, inject, input, OnChanges, SimpleChanges} from '@angular/core';
import {AccountsManagerService} from '../../services/accounts-manager/accounts-manager.service';
import {AccountModel} from '../../models/account.model';
import {MatCard, MatCardContent, MatCardHeader, MatCardTitle} from '@angular/material/card';
import {NgIf} from '@angular/common';

@Component({
  selector: 'app-account-header',
  imports: [
    MatCardHeader,
    MatCard,
    MatCardTitle,
    MatCardContent,
    NgIf
  ],
  templateUrl: './account-header.component.html',
  styleUrl: './account-header.component.scss'
})

export class AccountHeaderComponent implements OnChanges{
  readonly accountId = input.required<string>();
  private readonly accounts: AccountsManagerService = inject(AccountsManagerService);
  account: AccountModel = new AccountModel();

  ngOnChanges(changes:SimpleChanges) {
    if (changes['accountId']) {
      this.accounts.getAccounts().subscribe((accounts) => {
        this.account = accounts.find(a => a.id === this.accountId())!;
      });
    }
  }

  protected readonly Date = Date;
}
