export class PersonNameModel {
    public id: string;
    public prefix: string;
    public first: string;
    public middle: string;
    public last: string;
    public suffix: string;

    constructor() {
        this.id = '00000000-0000-0000-0000-000000000000';
        this.prefix = '';
        this.first = '';
        this.middle = '';
        this.last = '';
        this.suffix = '';
    }

    public get fullName(): string {
      const parts: string[] = [];
      if (this.prefix) parts.push(this.prefix);
      if (this.first) parts.push(this.first);
      if (this.middle) parts.push(this.middle);
      if (this.last) parts.push(this.last);
      if (this.suffix) parts.push(this.suffix);
      return parts.join(' ');
    }
}
