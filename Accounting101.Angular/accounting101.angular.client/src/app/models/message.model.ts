export class Message<T> {
  source = '';
  destination = '';
  type = '';
  message!: T;
}
