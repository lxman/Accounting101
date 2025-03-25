import {Component, inject} from '@angular/core';
import {ChartOfAccountsService} from '../../services/chart-of-accounts/chart-of-accounts.service';
import {ChartItemModel} from '../../models/chart-item.model';
import {MatCard, MatCardActions, MatCardContent, MatCardTitle} from '@angular/material/card';
import {MatButton} from '@angular/material/button';
import {NgForOf} from '@angular/common';
import {UserDataService} from '../../services/user-data/user-data.service';
import {GlobalConstantsService} from '../../services/global-constants/global-constants.service';
import {Router} from '@angular/router';

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
  private readonly coaService: ChartOfAccountsService = inject(ChartOfAccountsService);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);
  private readonly router: Router = inject(Router);
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
    this.coaService.createCoA(name, userId, clientId).subscribe(() => this.router.navigate(['/account-list']));
  }
}
