import {Component, inject} from '@angular/core';
import {ChartOfAccountsService} from '../../services/chart-of-accounts/chart-of-accounts.service';
import {ChartItemModel} from '../../models/chart-item.model';
import {MatCard, MatCardActions, MatCardContent, MatCardTitle} from '@angular/material/card';
import {MatButton} from '@angular/material/button';
import {NgForOf} from '@angular/common';
import {UserDataService} from '../../services/user-data/user-data.service';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';

@Component({
  selector: 'app-create-coa',
  imports: [
    MatCard,
    MatCardTitle,
    MatCardContent,
    MatCardActions,
    MatButton,
    NgForOf
  ],
  templateUrl: './create-coa.component.html',
  styleUrl: './create-coa.component.scss'
})
export class CreateCoaComponent {
  coaService: ChartOfAccountsService = inject(ChartOfAccountsService);
  userData: UserDataService = inject(UserDataService);
  globals: GlobalConstantsService = inject(GlobalConstantsService);
  names: string[] = [];
  models: ChartItemModel[] = [];

  constructor() {
    this.coaService.getAvailableChartNames().subscribe({
      next: (names) => {
        this.names = names;
        for (const name in this.names) {
          this.coaService.getDescription(name).subscribe(model => this.models.push(model));
        }
      }
    });
  }

  chartSelected(name: string) {
    const userId = this.userData.get(this.globals.userIdKey);
    const clientId = this.userData.get(this.globals.clientIdKey);
    this.coaService.createCoA(name, userId, clientId).subscribe();
    console.log(name);
  }
}
