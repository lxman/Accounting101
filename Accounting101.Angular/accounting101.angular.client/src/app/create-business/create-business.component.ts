import { Component } from '@angular/core';
import { BusinessModel } from '../../../Models/business.model';
import { BusinessManagerService } from '../../services/business-manager/business-manager.service';
import { FormControl, FormGroup, Validators } from '@angular/forms';

@Component({
    selector: 'app-create-business',
    templateUrl: './create-business.component.html',
    styleUrl: './create-business.component.scss'
})

export class CreateBusinessComponent {
  createBusinessForm = new FormGroup({
    name: new FormControl('', Validators.required),
  });
}
