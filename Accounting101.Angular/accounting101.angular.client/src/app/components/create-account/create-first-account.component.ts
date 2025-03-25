import {Component, inject} from '@angular/core';
import {MatButton} from '@angular/material/button';
import {Router} from '@angular/router';

@Component({
  selector: 'app-create-first-account',
  imports: [
    MatButton
  ],
  templateUrl: './create-first-account.component.html',
  styleUrl: './create-first-account.component.scss'
})
export class CreateFirstAccountComponent {
  private readonly router: Router = inject(Router);

  createChart() {
    this.router.navigate(['/create-coa']);
  }

  createAccount() {
    this.router.navigate(['/create-single']);
  }
}
