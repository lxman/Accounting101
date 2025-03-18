import { AccountGroupModel } from "./account-group.model";

export class RootGroups {
  clientId: string = '';
  assets: AccountGroupModel = new AccountGroupModel("Assets");
  liabilities: AccountGroupModel = new AccountGroupModel("Liabilities");
  equity: AccountGroupModel = new AccountGroupModel("Equity");
  revenue: AccountGroupModel = new AccountGroupModel("Revenue");
  expenses: AccountGroupModel = new AccountGroupModel("Expenses");
}
