import { AccountInfoModel } from "./account-info.model";
import { BaseAccountType } from "../enums/base-account-type.enum";

export class AccountModel {
  id: string = '00000000-0000-0000-0000-000000000000';
  type: BaseAccountType = BaseAccountType.asset;
  clientId: string = '00000000-0000-0000-0000-000000000000';
  infoId: string = '00000000-0000-0000-0000-000000000000';
  startBalance: number = 0;
  isDebitAccount: boolean = false;
  created: number = new Date().setHours(0, 0, 0, 0);
  info: AccountInfoModel = new AccountInfoModel();
}
