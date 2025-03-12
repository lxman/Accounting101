import { Component, inject } from '@angular/core';
import { CommonModule, NgIf, NgFor } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop'
import { MatCardModule } from '@angular/material/card';
import { AccountsManagerService } from '../../services/accounts-manager/accounts-manager.service';
import { ClientManagerService } from '../../services/client-manager/client-manager.service';
import { UserDataService } from '../../services/user-data/user-data.service';
import { GlobalConstantsService } from '../../services/global-constants/global-constants.service';

@Component({
  selector: 'app-client-selector',
  templateUrl: './client-selector.component.html',
  styleUrl: './client-selector.component.scss',
  imports: [MatCardModule, NgIf, NgFor, CommonModule],
  providers: [ClientManagerService]
})

export class ClientSelectorComponent {
  private readonly accountsService = inject(AccountsManagerService);
  private readonly clientService = inject(ClientManagerService);
  private readonly userDataService = inject(UserDataService);
  private readonly globals = inject(GlobalConstantsService);
  public clients = toSignal(this.clientService.getClients(), { initialValue: [] });

  clientSelected(clientId: string) {
    this.userDataService.set(this.globals.clientIdKey, clientId);
    this.accountsService.accountsExist()
      .subscribe((exists) => {
        if (exists) {
          console.log('Accounts exist for client: ', clientId);
        } else {
          console.log('No accounts for client: ', clientId);
        }
      }
    );
  }
}
