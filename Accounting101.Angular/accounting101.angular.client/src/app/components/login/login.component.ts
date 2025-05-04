import { Component, inject } from '@angular/core';
import { LoginModel } from '../../models/login.model';
import { UserClient } from '../../clients/user-client/user-client.service';
import { UserDataService } from '../../services/user-data/user-data.service';
import { BusinessClient } from '../../clients/business-client/business-client.service';
import { ClientClient } from '../../clients/client-client/client-client.service';
import { GlobalConstantsService } from '../../services/global-constants/global-constants.service';
import { MatSnackBar } from '@angular/material/snack-bar';
import { FormGroup, FormControl, Validators, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { MatCard, MatCardHeader, MatCardTitle, MatCardContent } from '@angular/material/card';
import { MatInput } from '@angular/material/input';
import { MatButton } from '@angular/material/button';
import { Idle  } from '@ng-idle/core';

@Component({
    selector: 'app-login',
    templateUrl: './login.component.html',
    styleUrl: './login.component.scss',
    imports: [
      MatCard,
      MatCardHeader,
      MatCardTitle,
      MatCardContent,
      FormsModule,
      ReactiveFormsModule,
      MatInput,
      MatButton
    ]
})
export class LoginComponent {
  private readonly globals: GlobalConstantsService = inject(GlobalConstantsService);
  private readonly userManager: UserClient = inject(UserClient);
  private readonly userData: UserDataService = inject(UserDataService);
  private readonly businessManager: BusinessClient = inject(BusinessClient);
  private readonly clientManager: ClientClient = inject(ClientClient);
  private readonly toastService: MatSnackBar = inject(MatSnackBar);
  private readonly router: Router = inject(Router);
  private readonly idle: Idle = inject(Idle);

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
        this.idle.watch();
        this.userData.set(this.globals.userIdKey, ApplicationUser.id);
        this.userData.set(this.globals.rolesKey, ApplicationUser.roles.join(','));
        this.businessManager.businessExists().subscribe({
          next: (exists) => {
            if (!exists) {
              void this.router.navigate(['/create-business']);
            }
            else {
              this.clientManager.getClientsExist().subscribe({
                next: (clientsExist) => {
                  if (clientsExist) {
                    void this.router.navigate(['/client-selector']);
                  } else {
                    void this.router.navigate(['/create-client']);
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
