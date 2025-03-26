import {Component, inject} from '@angular/core';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {AccountHeaderComponent} from '../../controls/account-header/account-header.component';
import {MenuComponent} from '../menu/menu.component';
import {Screen} from '../../enums/screen.enum';
import {TransactionListComponent} from '../../controls/transaction-list/transaction-list.component';
import {ClientHeaderComponent} from '../../controls/client-header/client-header.component';
import {ClientManagerService} from '../../services/client-manager/client-manager.service';
import {AsyncPipe} from '@angular/common';
import {MatDivider} from '@angular/material/divider';

@Component({
  selector: 'app-account',
  imports: [
    AccountHeaderComponent,
    MenuComponent,
    TransactionListComponent,
    ClientHeaderComponent,
    AsyncPipe,
    MatDivider
  ],
  templateUrl: './account.component.html',
  styleUrl: './account.component.scss'
})

export class AccountComponent {
  readonly accountId: string;
  private readonly globals = inject(GlobalConstantsService);
  private readonly userDataService = inject(UserDataService);
  private readonly clientManager = inject(ClientManagerService);
  readonly client = this.clientManager.getClient(this.userDataService.get(this.globals.clientIdKey));

  protected readonly Screen = Screen;

  constructor() {
    this.accountId = this.userDataService.get(this.globals.accountIdKey);
  }
}
