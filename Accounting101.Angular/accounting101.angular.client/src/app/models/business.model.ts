import { ForeignAddressModel } from "./foreign-address.model";
import { UsAddressModel } from "./us-address.model";

export class BusinessModel {
  public name: string;
  public address: UsAddressModel | ForeignAddressModel | null;

  constructor() {
    this.name = '';
    this.address = null;
  }
}
