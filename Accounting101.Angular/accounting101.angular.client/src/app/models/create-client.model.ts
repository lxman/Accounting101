export class CreateClientModel {
  id: string;
  businessName: string;
  personNameId: string;
  addressId: string;

  constructor(businessName: string, personNameId: string, addressId: string) {
    this.id = '00000000-0000-0000-0000-000000000000';
    this.businessName = businessName;
    this.personNameId = personNameId;
    this.addressId = addressId;
  }
}
