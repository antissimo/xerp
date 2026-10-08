#Agent-First ERP
Vizija: docs/vision.md. Arhitektura: docs/architecture.md. Specifikacije: docs/specs/.
Stack: C# / ASP.NET Core, PostgreSQL, React + TypeScript, MCP, CLI.
Slojevi: Domain -> Application -> Infrastructure -> Api. Web, CLI i MCP su tanki klijenti nad istim Application slojem.
Komande: `dotnet build`, `dotnet test`.
Pravilo: AI nije feature ERP-a, AI je korisnik ERP-a.
Svaki upit mora biti tenant-scoped.

