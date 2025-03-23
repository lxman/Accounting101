import {Component, inject, OnInit} from '@angular/core';
import { RouterOutlet } from '@angular/router';
import { IdleService } from './services/idle/idle.service';

@Component({
    selector: 'app-root',
    templateUrl: './app.component.html',
    styleUrls: ['../styles.scss', './app.component.scss'],
    imports: [RouterOutlet]
})

export class AppComponent implements OnInit{
  private readonly idleService: IdleService = inject(IdleService);
  title = 'accounting101.angular.client';

  ngOnInit() {
    this.idleService.initialize();
  }
}
