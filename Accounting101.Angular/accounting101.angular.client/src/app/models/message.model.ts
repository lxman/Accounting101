export class Message<T> {
  source = '';
  destination = '';
  message!: T;
}
