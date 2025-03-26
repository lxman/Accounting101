import { ComponentFixture, TestBed } from '@angular/core/testing';

import { FastEntryComponent } from './fast-entry.component';

describe('FastEntryComponent', () => {
  let component: FastEntryComponent;
  let fixture: ComponentFixture<FastEntryComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [FastEntryComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(FastEntryComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
