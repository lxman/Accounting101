import { Component } from '@angular/core';
import { CreateUserModel } from '../../../Models/create-user.model';
import { UserManagerService } from '../../services/user-manager/user-manager.service';
import { MatSnackBar } from '@angular/material/snack-bar';
import { FormGroup, FormControl, Validators, FormsModule, ReactiveFormsModule } from '@angular/forms';
import { MatCard, MatCardContent } from '@angular/material/card';
import { MatGridList } from '@angular/material/grid-list';
import { MatFormField, MatLabel, MatError } from '@angular/material/form-field';
import { MatInput } from '@angular/material/input';
import { NgIf } from '@angular/common';
import { MatButton } from '@angular/material/button';

@Component({
    selector: 'app-register-user',
    templateUrl: './register-user.component.html',
    styleUrl: './register-user.component.css',
    imports: [MatCard, MatCardContent, FormsModule, ReactiveFormsModule, MatGridList, MatFormField, MatLabel, MatInput, NgIf, MatError, MatButton]
})

export class RegisterUserComponent {
  registerForm = new FormGroup({
    firstName: new FormControl('', Validators.required),
    lastName: new FormControl('', Validators.required),
    password: new FormControl('', Validators.required),
    email: new FormControl('', Validators.required),
    phone: new FormControl('', Validators.required),
    role: new FormControl('', Validators.required)
  });
  f = this.registerForm.controls;
  submitted = false;

  constructor(private readonly userManager: UserManagerService, private readonly toastService: MatSnackBar
  ) {}

  onSubmit() {
    const user = new CreateUserModel();
    user.firstName = this.registerForm.get('firstName')!.value!;
    user.lastName = this.registerForm.get('lastName')!.value!;
    user.password = this.registerForm.get('password')!.value!;
    user.email = this.registerForm.get('email')!.value!;
    user.phoneNumber = this.registerForm.get('phone')!.value!;
    user.role = this.registerForm.get('role')!.value!;
    console.log(user);
    this.submitted = true;
    this.userManager.registerUser(user).subscribe({
      next: (u) => console.log(u),
      error: (e) => this.toastService.open('Error: ' + e.error[0].description, 'Close', {
        duration: 5000,
        verticalPosition: 'top',
        horizontalPosition: 'right'
      })
    });
  }
}
