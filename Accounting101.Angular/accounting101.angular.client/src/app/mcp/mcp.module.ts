import { NgModule } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule, ReactiveFormsModule } from '@angular/forms';
import { HttpClientModule } from '@angular/common/http';
import { RouterModule, Routes } from '@angular/router';

import { McpComponent } from './mcp.component';

const routes: Routes = [
  { path: 'mcp', component: McpComponent }
];

@NgModule({
  declarations: [
    McpComponent
  ],
  imports: [
    CommonModule,
    FormsModule,
    ReactiveFormsModule,
    HttpClientModule,
    RouterModule.forChild(routes)
  ],
  exports: [
    McpComponent
  ]
})
export class McpModule { }
