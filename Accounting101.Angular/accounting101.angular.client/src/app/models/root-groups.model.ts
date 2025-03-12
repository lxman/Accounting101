import { AccountGroupModel } from "./account-group.model";

export class RootGroups {
  clientId: string = '';
  assets: AccountGroupModel = new AccountGroupModel();
  liabilities: AccountGroupModel = new AccountGroupModel();
  equity: AccountGroupModel = new AccountGroupModel();
  revenue: AccountGroupModel = new AccountGroupModel();
  expenses: AccountGroupModel = new AccountGroupModel();
}
