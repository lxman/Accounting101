import { Component, inject } from '@angular/core';
import { CommonModule, NgIf, NgFor } from '@angular/common';
import { toSignal } from '@angular/core/rxjs-interop'
import { MatCardModule } from '@angular/material/card';
import { AccountsClient } from '../../clients/accounts-client/accounts-client.service';
import { ClientClient } from '../../clients/client-client/client-client.service';
import { UserDataService } from '../../services/user-data/user-data.service';
import { GlobalConstantsService } from '../../services/global-constants/global-constants.service';
import {MenuComponent} from '../menu/menu.component';
import {MatButton} from '@angular/material/button';
import {MatMenuModule} from '@angular/material/menu';
import {Router} from '@angular/router';
import {Screen} from '../../enums/screen.enum';
import {DefaultLayoutAlignDirective, DefaultLayoutDirective} from '@ngbracket/ngx-layout';
import {DeleteClientConfirm} from '../../dialogs/delete-client-confirm/delete-client-confirm.component';
import {MatDialog} from '@angular/material/dialog';
import {BehaviorSubject, switchMap} from 'rxjs';

@Component({
  selector: 'app-client-selector',
  templateUrl: './client-selector.component.html',
  styleUrls: ['./client-selector.component.scss'],
  imports: [
    MatCardModule,
    NgIf,
    NgFor,
    CommonModule,
    MenuComponent,
    MatButton,
    MatMenuModule,
    DefaultLayoutDirective,
    DefaultLayoutAlignDirective
  ],
  providers: [ClientClient]
})

export class ClientSelectorComponent {
  private refreshTrigger = new BehaviorSubject<void>(undefined)
  private readonly accountsService = inject(AccountsClient);
  private readonly clientService = inject(ClientClient);
  private readonly userDataService = inject(UserDataService);
  private readonly globals = inject(GlobalConstantsService);
  private readonly router = inject(Router);
  private readonly dialog = inject(MatDialog);
  public clients = this.refreshTrigger.pipe(switchMap(() => this.clientService.getClients()));
  clientsSignal = toSignal(this.clients, { initialValue: [] });
  protected readonly Screen = Screen;

  clientSelected(clientId: string) {
    this.userDataService.set(this.globals.clientIdKey, clientId);
    this.accountsService.accountsExist()
      .subscribe((exists) => {
        if (exists) {
          void this.router.navigate(['/account-list']);
        } else {
          void this.router.navigate(['/create-account']);
        }
      }
    );
  }

  clientDeleteClicked(clientId: string) {
    const dialogRef = this.dialog.open(DeleteClientConfirm, {
      data: {confirm: false},
      autoFocus: 'dialog'
    });
    dialogRef.afterClosed().subscribe(result => {
      if (result) {
        this.clientService.deleteClient(clientId).subscribe(
          (success) =>
          {
            if (success) {
              this.refreshTrigger.next();
              this.clients.subscribe(() => {
                if (this.clientsSignal().length == 0) {
                  void this.router.navigate(['/create-client']);
                  return;
                }
                void this.router.navigate(['/client-selector'])
              });
            } else {
              console.error('Failed to delete client');
            }
          }
        );
      }
    });
  }
}
