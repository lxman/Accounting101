import { ComponentFixture, TestBed } from '@angular/core/testing';

import { TransactionLineItemComponent } from './transaction-line-item.component';

describe('TransactionLineItemComponent', () => {
  let component: TransactionLineItemComponent;
  let fixture: ComponentFixture<TransactionLineItemComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [TransactionLineItemComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(TransactionLineItemComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
