import {Component, inject, input} from '@angular/core';
import {DefaultLayoutAlignDirective, DefaultLayoutDirective} from '@ngbracket/ngx-layout';
import {MatCard, MatCardContent, MatCardHeader, MatCardTitle} from '@angular/material/card';
import {NgIf} from '@angular/common';
import {ClientModel} from '../../models/client.model';
import {Router} from '@angular/router';

@Component({
  selector: 'app-client-header',
  imports: [
    DefaultLayoutAlignDirective,
    DefaultLayoutDirective,
    MatCard,
    MatCardContent,
    MatCardHeader,
    MatCardTitle,
    NgIf
  ],
  templateUrl: './client-header.component.html',
  styleUrl: './client-header.component.scss'
})

export class ClientHeaderComponent {
  readonly client = input.required<ClientModel | null>();
  private readonly router: Router = inject(Router);

  clientHeaderClicked() {
    void this.router.navigate(['/client-selector']);
  }
}
