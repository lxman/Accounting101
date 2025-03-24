import {AccountGroupListItem} from './account-group-list-item';

export class AccountGroupModel {
  id: string = '';
  name: string;
  items: AccountGroupListItem[] = [];

  constructor(name: string) {
    this.name = name;
  }
}
