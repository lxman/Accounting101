import { PersonNameModel } from "./person-name.model";
import { UsAddressModel } from "./us-address.model";
import { ForeignAddressModel } from "./foreign-address.model";
import { AddressModel } from "./address.model";

export class ClientModel {
  id: string;
  businessName: string;
  contactName: PersonNameModel;
  address: UsAddressModel | ForeignAddressModel;

  constructor(businessName: string, contact: PersonNameModel, address: AddressModel) {
    this.id = '';
    this.businessName = businessName;
    this.contactName = contact;
    if (address.isForeign) {
      this.address = new ForeignAddressModel(address.line1, address.line2, address.stateProvince, address.postalCode, address.country);
    } else {
      this.address = new UsAddressModel(address.line1, address.line2, address.city, address.stateProvince, address.postalCode);
    }
  }
}
