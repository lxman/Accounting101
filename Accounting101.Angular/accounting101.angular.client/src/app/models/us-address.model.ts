import { Address } from "./address.interface";

export class UsAddressModel implements Address {
  public id: string;
  public isForeign: boolean;
  public line1: string;
  public line2: string;
  public city: string;
  public state: string;
  public zip: string;
  public readonly country: string;

  constructor(line1: string, line2: string, city: string, state: string, zip: string) {
    this.id = '';
    this.isForeign = false;
    this.line1 = line1;
    this.line2 = line2;
    this.city = city;
    this.state = state;
    this.zip = zip;
    this.country = 'US';
  }

  public get asString(): string {
    const parts: string[] = [];
    if (this.line1) parts.push(this.line1);
    if (this.line2) parts.push(this.line2);
    if (this.city) parts.push(this.city);
    if (this.state) parts.push(this.state);
    if (this.zip) parts.push(this.zip);
    return parts.join(', ');
  }
}
