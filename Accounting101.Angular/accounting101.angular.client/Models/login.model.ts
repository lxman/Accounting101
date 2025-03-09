export class LoginModel {
  public email: string;
  public password: string;
  public twoFactorAuthenticationCode: string;
  public twoFactorAuthenticationCodeReset: string;

  constructor() {
    this.email = '';
    this.password = '';
    this.twoFactorAuthenticationCode = '';
    this.twoFactorAuthenticationCodeReset = '';
  }
}
