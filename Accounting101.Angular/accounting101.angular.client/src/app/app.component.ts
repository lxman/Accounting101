import { Component, OnInit } from '@angular/core';
import { RouterOutlet } from '@angular/router';

@Component({
    selector: 'app-root',
    templateUrl: './app.component.html',
    styleUrls: ['../styles.scss', './app.component.scss'],
    imports: [RouterOutlet]
})

export class AppComponent implements OnInit {
  constructor() {}

  ngOnInit() {
  }
  title = 'angulartest.client';
}
