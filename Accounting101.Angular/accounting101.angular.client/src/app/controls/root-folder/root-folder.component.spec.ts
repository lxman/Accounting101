import { ComponentFixture, TestBed } from '@angular/core/testing';

import { RootFolderComponent } from './root-folder.component';

describe('RootFolderComponent', () => {
  let component: RootFolderComponent;
  let fixture: ComponentFixture<RootFolderComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [RootFolderComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(RootFolderComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
