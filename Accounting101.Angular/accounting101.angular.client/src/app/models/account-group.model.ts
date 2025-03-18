export class AccountGroupModel {
  id: string = '';
  name: string;
  groups?: AccountGroupModel[];
  accounts?: string[];

  constructor(name: string) {
    this.name = name;
  }
}
