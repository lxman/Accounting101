import { Address } from "./address.interface";

export class BusinessModel {
  public name: string;
  public address: Address;
  constructor() {
    this.name = '';
    this.address = {} as Address;
  }
}
