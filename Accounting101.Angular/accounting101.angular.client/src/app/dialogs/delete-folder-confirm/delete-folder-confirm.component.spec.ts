import { ComponentFixture, TestBed } from '@angular/core/testing';

import { DeleteFolderConfirmComponent } from './delete-folder-confirm.component';

describe('DeleteFolderConfirmComponent', () => {
  let component: DeleteFolderConfirmComponent;
  let fixture: ComponentFixture<DeleteFolderConfirmComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeleteFolderConfirmComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(DeleteFolderConfirmComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
