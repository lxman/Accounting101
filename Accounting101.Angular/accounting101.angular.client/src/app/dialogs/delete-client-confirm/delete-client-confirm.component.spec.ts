import { ComponentFixture, TestBed } from '@angular/core/testing';

import { DeleteClientConfirm } from './delete-client-confirm.component';

describe('DeleteClientWarningComponent', () => {
  let component: DeleteClientConfirm;
  let fixture: ComponentFixture<DeleteClientConfirm>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeleteClientConfirm]
    })
    .compileComponents();

    fixture = TestBed.createComponent(DeleteClientConfirm);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
