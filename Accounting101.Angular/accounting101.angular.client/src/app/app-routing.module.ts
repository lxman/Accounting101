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
import { AuthGuard } from './auth.guard';

export const routes: Routes = [
  { path: '', component: LoginComponent},
  { path: 'create-business', component: CreateBusinessComponent, canActivate: [AuthGuard] },
  { path: 'create-client', component: CreateClientComponent, canActivate: [AuthGuard] },
  { path: 'client-selector', component: ClientSelectorComponent, canActivate: [AuthGuard] },
  { path: 'create-account', component: CreateAccountComponent, canActivate: [AuthGuard]},
  { path: 'create-coa', component: CreateCoaComponent, canActivate: [AuthGuard]},
  { path: 'create-single', component: CreateSingleAccountComponent, canActivate: [AuthGuard]},
  { path: 'account-list', component: AccountListComponent, canActivate: [AuthGuard]}
];

@NgModule({
  imports: [RouterModule.forRoot(routes)],
  exports: [RouterModule]
})

export class AppRoutingModule { }
