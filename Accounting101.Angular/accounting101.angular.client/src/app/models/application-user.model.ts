export class ApplicationUser {
  id: string = '';
  firstName: string = '';
  lastName: string = '';
  userName: string = '';
  normalizedUserName: string = '';
  email: string = '';
  normalizedEmail: string = '';
  emailConfirmed: boolean = false;
  passwordHash: string = '';
  securityStamp: string = '';
  concurrencyStamp: string = '';
  phoneNumber: string = '';
  phoneNumberConfirmed: boolean = false;
  twoFactorEnabled: boolean = false;
  lockoutEnd: Date = new Date();
  lockoutEnabled: boolean = false;
  accessFailedCount: number = 0;
  roles: string[] = [];
}
