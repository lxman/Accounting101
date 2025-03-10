import { ComponentFixture, TestBed } from '@angular/core/testing';

import { PersonNameComponent } from './person-name.component';

describe('PersonNameComponent', () => {
  let component: PersonNameComponent;
  let fixture: ComponentFixture<PersonNameComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      declarations: [PersonNameComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(PersonNameComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
