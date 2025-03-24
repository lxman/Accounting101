import {Component, inject} from '@angular/core';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {UserDataService} from '../../services/user-data/user-data.service';
import {AccountHeaderComponent} from '../../controls/account-header/account-header.component';
import {MenuComponent} from '../menu/menu.component';
import {Screen} from '../../enums/screen.enum';

@Component({
  selector: 'app-account',
  imports: [
    AccountHeaderComponent,
    MenuComponent
  ],
  templateUrl: './account.component.html',
  styleUrl: './account.component.scss'
})

export class AccountComponent {
  readonly accountId: string;
  private readonly globals = inject(GlobalConstantsService);
  private readonly userDataService = inject(UserDataService);

  constructor() {
    this.accountId = this.userDataService.get(this.globals.accountIdKey);
  }

  protected readonly Screen = Screen;
}
