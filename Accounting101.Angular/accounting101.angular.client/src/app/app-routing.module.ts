import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LoginComponent } from './components/login/login.component';
import { CreateBusinessComponent } from './components/create-business/create-business.component';
import { CreateClientComponent } from './components/create-client/create-client.component';
import { ClientSelectorComponent } from './components/client-selector/client-selector.component';

const routes: Routes = [
  { path: '', component: LoginComponent},
  { path: 'create-business', component: CreateBusinessComponent },
  { path: 'create-client', component: CreateClientComponent },
  { path: 'client-selector', component: ClientSelectorComponent }
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})

export class AppRoutingModule { }
