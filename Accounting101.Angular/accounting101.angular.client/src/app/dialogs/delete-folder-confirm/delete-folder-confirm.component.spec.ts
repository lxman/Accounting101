import { ComponentFixture, TestBed } from '@angular/core/testing';

import { DeleteFolderConfirm } from './delete-folder-confirm.component';

describe('DeleteFolderConfirm', () => {
  let component: DeleteFolderConfirm;
  let fixture: ComponentFixture<DeleteFolderConfirm>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [DeleteFolderConfirm]
    })
    .compileComponents();

    fixture = TestBed.createComponent(DeleteFolderConfirm);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
