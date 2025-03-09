export class CreateUserModel {
  public firstName: string;
  public lastName: string;
  public email: string;
  public password: string;
  public phoneNumber: string;
  public role: string;

  constructor() {
    this.firstName = '';
    this.lastName = '';
    this.email = '';
    this.password = '';
    this.phoneNumber = '';
    this.role = '';
  }
}
