import {Component, input} from '@angular/core';
import {TransactionModel} from '../../models/transaction.model';

@Component({
  selector: 'app-transaction-line-item',
  imports: [],
  templateUrl: './transaction-line-item.component.html',
  styleUrl: './transaction-line-item.component.scss'
})
export class TransactionLineItemComponent {
  private readonly transaction = input.required<TransactionModel>();
}
