export class Transaction {
  id: string = '';
  creditAccountId: string = '';
  debitAccountId: string = '';
  amount: number = 0;
  when: number = new Date().setHours(0, 0, 0, 0);
}
