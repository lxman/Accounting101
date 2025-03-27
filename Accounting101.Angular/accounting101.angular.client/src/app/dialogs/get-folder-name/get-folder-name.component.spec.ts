import { ComponentFixture, TestBed } from '@angular/core/testing';

import { GetFolderName } from './get-folder-name.component';

describe('GetFolderName', () => {
  let component: GetFolderName;
  let fixture: ComponentFixture<GetFolderName>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [GetFolderName]
    })
    .compileComponents();

    fixture = TestBed.createComponent(GetFolderName);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
