import {Component, inject} from '@angular/core';
import {MatButton} from '@angular/material/button';
import {Router} from '@angular/router';

@Component({
  selector: 'app-create-account',
  imports: [
    MatButton
  ],
  templateUrl: './create-account.component.html',
  styleUrl: './create-account.component.scss'
})
export class CreateAccountComponent {
  private readonly router: Router = inject(Router);

  createChart() {
    this.router.navigate(['/create-coa']);
  }

  createAccount() {
    this.router.navigate(['/create-single']);
  }
}
