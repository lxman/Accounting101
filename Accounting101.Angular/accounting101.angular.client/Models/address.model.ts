import { Address } from "./address.interface";

export class AddressModel implements Address {
  public isForeign: boolean;
  public line1: string;
  public line2: string;
  public city: string;
  public stateProvince: string;
  public postalCode: string;
  public country: string;

  constructor() {
    this.isForeign = false;
    this.line1 = '';
    this.line2 = '';
    this.city = '';
    this.stateProvince = '';
    this.postalCode = '';
    this.country = '';
  }
}
