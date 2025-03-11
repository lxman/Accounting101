import { PersonNameModel } from "./person-name.model";
import { UsAddressModel } from "./us-address.model";
import { ForeignAddressModel } from "./foreign-address.model";
import { AddressModel } from "./address.model";

export class ClientModel {
  id: string;
  businessName: string;
  contactName: PersonNameModel;
  address: AddressModel;
  usAddress: UsAddressModel | null = null;
  foreignAddress: ForeignAddressModel | null = null;

  constructor(businessName: string, contact: PersonNameModel, address: AddressModel) {
    this.id = '';
    this.businessName = businessName;
    this.contactName = contact;
    this.address = address;
  }
}
