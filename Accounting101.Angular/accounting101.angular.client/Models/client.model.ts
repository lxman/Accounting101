import { PersonNameModel } from "./person-name.model";
import { Address } from "./address.interface";
import { UsAddressModel } from "./us-address.model";
import { ForeignAddressModel } from "./foreign-address.model";

export class ClientModel {
  id: string;
  businessName: string;
  contactName: PersonNameModel | null = null;
  address: UsAddressModel | ForeignAddressModel | null = null;

  constructor(address: Address | null = null) {
    this.id = '';
    this.businessName = '';
  }
}
