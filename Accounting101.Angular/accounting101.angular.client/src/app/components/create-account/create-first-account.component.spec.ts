import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CreateFirstAccountComponent } from './create-first-account.component';

describe('CreateAccountComponent', () => {
  let component: CreateFirstAccountComponent;
  let fixture: ComponentFixture<CreateFirstAccountComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CreateFirstAccountComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(CreateFirstAccountComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
