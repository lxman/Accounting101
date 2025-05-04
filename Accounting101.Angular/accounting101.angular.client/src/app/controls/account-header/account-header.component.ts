import {Component, inject, input, OnChanges, SimpleChanges} from '@angular/core';
import {AccountsClient} from '../../clients/accounts-client/accounts-client.service';
import {AccountModel} from '../../models/account.model';
import {MatCard, MatCardContent, MatCardHeader, MatCardTitle} from '@angular/material/card';
import {Router} from '@angular/router';

@Component({
  selector: 'app-account-header',
  imports: [
    MatCardHeader,
    MatCard,
    MatCardTitle,
    MatCardContent
  ],
  templateUrl: './account-header.component.html',
  styleUrl: './account-header.component.scss'
})

export class AccountHeaderComponent implements OnChanges{
  // readonly accountId = input.required<string>();
  readonly account = input.required<AccountModel>();
  private readonly accounts: AccountsClient = inject(AccountsClient);
  private readonly router: Router = inject(Router);
  // account: AccountModel = new AccountModel();
  protected readonly Date = Date;

  ngOnChanges(changes:SimpleChanges) {
    // if (changes['accountId']) {
    //   this.accounts.getAccounts().subscribe((accounts) => {
    //     this.account = accounts.find(a => a.id === this.accountId())!;
    //   });
    // }
  }

  headerClicked() {
    void this.router.navigate(['/account-list']);
  }
}
