import { Component, OnInit } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { FormBuilder, FormGroup, Validators } from '@angular/forms';

@Component({
  selector: 'app-mcp',
  templateUrl: './mcp.component.html',
  styleUrls: ['./mcp.component.css']
})
export class McpComponent implements OnInit {
  toolsForm: FormGroup;
  availableTools: any[] = [];
  selectedTool: any = null;
  toolResponse: string = '';
  loading: boolean = false;
  error: string = '';

  constructor(
    private http: HttpClient,
    private fb: FormBuilder
  ) {
    this.toolsForm = this.fb.group({
      selectedToolName: ['', Validators.required],
      arguments: this.fb.group({})
    });
  }

  ngOnInit(): void {
    this.loadTools();
  }

  loadTools(): void {
    this.loading = true;
    this.error = '';

    const payload = {
      jsonrpc: '2.0',
      id: 'list_tools_request',
      method: 'list_tools'
    };

    this.http.post('/mcp', payload).subscribe({
      next: (response: any) => {
        this.availableTools = response.result.tools || [];
        this.loading = false;
      },
      error: (err) => {
        this.error = 'Failed to load tools: ' + (err.message || 'Unknown error');
        this.loading = false;
      }
    });
  }

  onToolSelect(toolName: string): void {
    this.selectedTool = this.availableTools.find(t => t.name === toolName);
    
    // Reset arguments form group
    const argGroup = this.fb.group({});
    
    if (this.selectedTool && this.selectedTool.input_schema && 
        this.selectedTool.input_schema.properties) {
      
      const properties = this.selectedTool.input_schema.properties;
      const required = this.selectedTool.input_schema.required || [];
      
      Object.keys(properties).forEach(propName => {
        const isRequired = required.includes(propName);
        argGroup.addControl(
          propName, 
          this.fb.control('', isRequired ? Validators.required : null)
        );
      });
    }
    
    this.toolsForm.setControl('arguments', argGroup);
  }

  executeTool(): void {
    if (this.toolsForm.invalid) {
      return;
    }

    this.loading = true;
    this.error = '';
    this.toolResponse = '';

    const toolName = this.toolsForm.get('selectedToolName')?.value;
    const args = this.toolsForm.get('arguments')?.value;

    const payload = {
      jsonrpc: '2.0',
      id: 'execute_tool_request',
      method: 'call_tool',
      params: {
        name: toolName,
        arguments: args
      }
    };

    this.http.post('/mcp', payload).subscribe({
      next: (response: any) => {
        if (response.result && response.result.content) {
          // Typically, the first content item with type 'text' contains the response
          const textContent = response.result.content.find((c: any) => c.type === 'text');
          this.toolResponse = textContent ? textContent.text : JSON.stringify(response.result);
        } else {
          this.toolResponse = JSON.stringify(response.result);
        }
        this.loading = false;
      },
      error: (err) => {
        this.error = 'Failed to execute tool: ' + (err.message || 'Unknown error');
        this.loading = false;
      }
    });
  }

  // Helper methods for the template
  objectKeys(obj: any): string[] {
    return obj ? Object.keys(obj) : [];
  }

  isRequired(propName: string): boolean {
    return this.selectedTool && 
           this.selectedTool.input_schema && 
           this.selectedTool.input_schema.required && 
           this.selectedTool.input_schema.required.includes(propName);
  }

  getInputType(propName: string): string {
    if (!this.selectedTool || !this.selectedTool.input_schema || !this.selectedTool.input_schema.properties) {
      return 'text';
    }

    const prop = this.selectedTool.input_schema.properties[propName];
    if (!prop || !prop.type) {
      return 'text';
    }

    switch (prop.type) {
      case 'number':
      case 'integer':
        return 'number';
      case 'boolean':
        return 'checkbox';
      default:
        return 'text';
    }
  }
}
