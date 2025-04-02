import { TestBed } from '@angular/core/testing';
import { MockBuilder } from 'ng-mocks';
import { RouterTestingModule } from '@angular/router/testing';
import { AppComponent } from './app.component';
import { IdleService } from './services/idle/idle.service';

describe('AppComponent', () => {
  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [RouterTestingModule, AppComponent]
    });
    return MockBuilder(AppComponent).mock(IdleService);
  });

  it(`should return app title`, () => {
    const fixture = TestBed.createComponent(AppComponent);
    const app = fixture.componentInstance;
    let result = app.getTitle();
    expect(result).toEqual('Accounting 101');
  });
});
