import { Address } from "./address.interface";

export class ForeignAddressModel implements Address {
  public id: string;
  public isForeign: boolean;
  public line1: string;
  public line2: string;
  public province: string;
  public postCode: string;
  public country: string;

  constructor(line1: string, line2: string, province: string, postCode: string, country: string) {
    this.id = '';
    this.isForeign = true;
    this.line1 = line1;
    this.line2 = line2;
    this.province = province;
    this.postCode = postCode;
    this.country = country;
  }
}
