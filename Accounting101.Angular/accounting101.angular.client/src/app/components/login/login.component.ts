import { Component, inject } from '@angular/core';
import { LoginModel } from '../../models/login.model';
import { UserManagerService } from '../../services/user-manager/user-manager.service';
import { UserDataService } from '../../services/user-data/user-data.service';
import { BusinessManagerService } from '../../services/business-manager/business-manager.service';
import { ClientManagerService } from '../../services/client-manager/client-manager.service';
import { GlobalConstantsService } from '../../services/global-constants/global-constants.service';
import { MatSnackBar } from '@angular/material/snack-bar';
import { FormGroup, FormControl, Validators, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatCard, MatCardHeader, MatCardTitle, MatCardContent } from '@angular/material/card';
import { MatFormField, MatLabel } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { MatButton } from '@angular/material/button';
import { ApplicationUser } from '../../models/application-user.model';

@Component({
    selector: 'app-login',
    templateUrl: './login.component.html',
    styleUrl: './login.component.scss',
    imports: [MatCard, MatCardHeader, MatCardTitle, MatCardContent, FormsModule, ReactiveFormsModule, MatFormField, MatLabel, MatInput, MatButton]
})
export class LoginComponent {
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);
  private readonly userManager: UserManagerService = inject(UserManagerService);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly businessManager: BusinessManagerService = inject(BusinessManagerService);
  private readonly clientManager: ClientManagerService = inject(ClientManagerService);
  private readonly toastService: MatSnackBar = inject(MatSnackBar);
  private readonly router: Router = inject(Router);

  loginForm = new FormGroup({
    email: new FormControl('', [Validators.required, Validators.email]),
    password: new FormControl('', Validators.required),
    twoFactorAuthenticationCode: new FormControl(''),
    twoFactorAuthenticationCodeReset: new FormControl('')
  });
  f = this.loginForm.controls;

  onSubmit() {
    const login = new LoginModel();
    login.email = this.loginForm.get('email')!.value!;
    login.password = this.loginForm.get('password')!.value!;
    // login.twoFactorAuthenticationCode = this.loginForm.get('twoFactorAuthenticationCode')!.value!;
    // login.twoFactorAuthenticationCodeReset = this.loginForm.get('twoFactorAuthenticationCodeReset')!.value!;
    this.userManager.loginUser(login).subscribe({
      next: (ApplicationUser) => {
        this.userData.set(this.globals.userIdKey, ApplicationUser.id);
        this.userData.set(this.globals.rolesKey, ApplicationUser.roles.join(','));
        this.businessManager.businessExists().subscribe({
          next: (exists) => {
            if (!exists) {
              this.router.navigate(['/create-business']);
            }
            else {
              this.clientManager.getClientsExist().subscribe({
                next: (clientsExist) => {
                  if (clientsExist) {
                    this.router.navigate(['/client-selector']);
                  } else {
                    this.router.navigate(['/create-client']);
                  }
                },
                error: (error) => {
                  console.error(error);
                }
              });
            }
          },
          error: (error) => {
            console.error(error);
          }
        });
      },
      error: (error) => {
        this.toastService.open('Login failed', 'Close', {
          duration: 3000,
          verticalPosition: 'top',
          horizontalPosition: 'center'
        });
        console.error(error);
      },
    });
  }
}
