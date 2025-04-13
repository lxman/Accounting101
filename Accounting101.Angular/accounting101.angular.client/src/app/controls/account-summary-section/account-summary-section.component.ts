import { Component, Input, OnChanges, SimpleChanges } from '@angular/core';
import { CommonModule } from '@angular/common';
import { MatCardModule } from '@angular/material/card';
import { MatExpansionModule } from '@angular/material/expansion';
import { AccountModel } from '../../models/account.model';
import { ReportGroupModel } from '../../models/report-group.interface';
import { AccountsClient } from '../../clients/accounts-client/accounts-client.service';

@Component({
  selector: 'app-account-summary-section',
  imports: [CommonModule, MatCardModule, MatExpansionModule],
  templateUrl: './account-summary-section.component.html',
  styleUrl: './account-summary-section.component.scss'
})

export class AccountSummarySection implements OnChanges {
  @Input() title: string = '';
  @Input() accounts: AccountModel[] = [];
  @Input() accountGroup?: ReportGroupModel;
  @Input() expanded: boolean = false;
  @Input() showDetails: boolean = false;

  totalBalance: number = 0;
  accountMap: Map<string, AccountModel> = new Map();
  balanceMap: Map<string, number> = new Map(); // Store account balances separately

  constructor(private accountsClient: AccountsClient) {}

  ngOnChanges(changes: SimpleChanges): void {
    // Create a lookup map for accounts
    this.accountMap.clear();
    this.accounts.forEach(account => {
      this.accountMap.set(account.id, account);
    });

    // Load balances for all accounts, then calculate total
    this.loadAccountBalances().then(() => {
      this.calculateTotal();
    });
  }

  async loadAccountBalances(): Promise<void> {
    const balancePromises = this.accounts.map(account => {
      return new Promise<void>((resolve) => {
        this.accountsClient.getBalanceOnDate(account.id, new Date()).subscribe(balance => {
          this.balanceMap.set(account.id, balance);
          resolve();
        });
      });
    });

    return Promise.all(balancePromises).then(() => {});
  }

  calculateTotal(): void {
    if (this.accountGroup) {
      this.totalBalance = this.calculateGroupTotal(this.accountGroup);
    } else {
      this.totalBalance = 0;
    }
  }

  calculateGroupTotal(group: ReportGroupModel): number {
    let total = 0;

    // Add balances from direct accounts in this group
    if (group.accounts) {
      for (const accountId of group.accounts) {
        total += this.balanceMap.get(accountId) || 0;
      }
    }

    // Add balances from child groups
    if (group.children) {
      for (const child of group.children) {
        total += this.calculateGroupTotal(child);
      }
    }

    return total;
  }

  getAccountName(accountId: string): string {
    return this.accountMap.get(accountId)?.info.name || 'Unknown Account';
  }

  getAccountBalance(accountId: string): number {
    return this.balanceMap.get(accountId) || 0;
  }
}
