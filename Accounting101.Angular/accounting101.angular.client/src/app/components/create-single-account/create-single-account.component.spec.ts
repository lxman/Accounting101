import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CreateSingleAccountComponent } from './create-single-account.component';

describe('CreateSingleAccountComponent', () => {
  let component: CreateSingleAccountComponent;
  let fixture: ComponentFixture<CreateSingleAccountComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CreateSingleAccountComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(CreateSingleAccountComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
