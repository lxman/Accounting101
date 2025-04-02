import { Injectable } from '@angular/core';
import { Subject } from 'rxjs';
import { Message } from '../../models/message.model';

@Injectable({
  providedIn: 'root'
})

export class MessageService<T> {
  private messageSource = new Subject<Message<T>>();
  message$ = this.messageSource.asObservable();

  sendMessage(message: Message<T>) {
    this.messageSource.next(message);
  }
}
