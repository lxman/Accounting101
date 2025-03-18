import { NgModule } from '@angular/core';
import { RouterModule, Routes } from '@angular/router';
import { LoginComponent } from './components/login/login.component';
import { CreateBusinessComponent } from './components/create-business/create-business.component';
import { CreateClientComponent } from './components/create-client/create-client.component';
import { ClientSelectorComponent } from './components/client-selector/client-selector.component';
import {CreateAccountComponent} from './components/create-account/create-account.component';
import {CreateCoaComponent} from './components/create-coa/create-coa.component';
import {CreateSingleAccountComponent} from './components/create-single-account/create-single-account.component';
import {AccountListComponent} from './components/account-list/account-list.component';

const routes: Routes = [
  { path: '', component: LoginComponent},
  { path: 'create-business', component: CreateBusinessComponent },
  { path: 'create-client', component: CreateClientComponent },
  { path: 'client-selector', component: ClientSelectorComponent },
  { path: 'create-account', component: CreateAccountComponent},
  { path: 'create-coa', component: CreateCoaComponent},
  { path: 'create-single', component: CreateSingleAccountComponent},
  { path: 'account-list', component: AccountListComponent}
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})

export class AppRoutingModule { }
