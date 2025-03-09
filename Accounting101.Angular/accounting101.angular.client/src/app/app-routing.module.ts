import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { AddressComponent } from './address/address.component';
import { LoginComponent } from './login/login.component';
import { CreateBusinessComponent } from './create-business/create-business.component';
import { CreateClientComponent } from './create-client/create-client.component';
import { ClientSelectorComponent } from './client-selector/client-selector.component';

const routes: Routes = [
  { path: '', component: LoginComponent},
  { path: 'address', component: AddressComponent },
  { path: 'create-business', component: CreateBusinessComponent },
  { path: 'create-client', component: CreateClientComponent },
  { path: 'client-selector', component: ClientSelectorComponent }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})

export class AppRoutingModule { }
