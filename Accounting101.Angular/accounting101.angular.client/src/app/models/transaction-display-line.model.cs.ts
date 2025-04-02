export class TransactionDisplayLine {
  id = '';
  when = '';
  debit: number | null = null;
  credit: number | null = null;
  balance = 0;
  otherAccount: string = '';
  selected = false;
}
