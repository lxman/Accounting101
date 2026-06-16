# UI

Front-end projects live here, one folder per UI — to be added as we go:

- `WPF/` — desktop client (planned)
- `Angular/` — web client (planned)

Dependency direction is one-way: UI projects depend on `../Backend`, never the
reverse. The domain core (`Accounting101.Ledger.Core`) is UI-agnostic and has no
reference to anything under this folder.
