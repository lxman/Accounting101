import { PersonNameModel } from "./person-name.model";
import { Address } from "./address.interface";

export class ClientModel implements Address {
  id: string;
  businessName: string;
  contactName: PersonNameModel | null = null;
  isForeign: boolean = false;
  line1: string;
  line2: string | undefined;

  constructor(address: Address | null = null) {
    this.id = '';
    this.businessName = '';
    this.contactName = new PersonNameModel();
    this.isForeign = address?.isForeign ?? false;
    this.line1 = address?.line1 ?? '';
    this.line2 = address?.line2;
  }
}
