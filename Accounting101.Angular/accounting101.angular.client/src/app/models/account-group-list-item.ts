import {AccountGroupListItemType} from '../enums/account-group-list-item-type.enum';
import {AccountGroupModel} from './account-group.model';

export class AccountGroupListItem {
  type: AccountGroupListItemType = AccountGroupListItemType.group;
  accountId: string | null = null;
  accountGroup: AccountGroupModel | null = null;
}
