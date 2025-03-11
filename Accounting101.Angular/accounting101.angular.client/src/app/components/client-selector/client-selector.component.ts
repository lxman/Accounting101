import { Component } from '@angular/core';
import { MatCardModule } from '@angular/material/card';
import { ClientModel } from '../../models/client.model';
import { ClientManagerService } from '../../services/client-manager/client-manager.service';

@Component({
  selector: 'app-client-selector',
  templateUrl: './client-selector.component.html',
  styleUrl: './client-selector.component.scss',
  imports: [MatCardModule]
})
export class ClientSelectorComponent {
  public clients: ClientModel[] = [];

  constructor(private clientManagerService: ClientManagerService) {
    this.clientManagerService.getClients().subscribe((clients: ClientModel[]) => {
      this.clients = clients;
    }
  )};

  clientSelected(client: string) {
    console.log('Client selected is: ', client);
  }
}
