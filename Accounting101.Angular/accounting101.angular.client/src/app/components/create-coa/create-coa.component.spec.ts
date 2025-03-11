import { ComponentFixture, TestBed } from '@angular/core/testing';

import { CreateCoaComponent } from './create-coa.component';

describe('CreateCoaComponent', () => {
  let component: CreateCoaComponent;
  let fixture: ComponentFixture<CreateCoaComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CreateCoaComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(CreateCoaComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
