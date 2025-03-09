export class CheckpointModel {
  public id: string;
  public clientId: string;
  public date: number;

  constructor() {
    this.id = '';
    this.clientId = '';
    this.date = new Date().setHours(0, 0, 0, 0);
  }
}
