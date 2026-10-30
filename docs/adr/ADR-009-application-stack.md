# ADR-009 Application stack

- Context: standard, well supported, license-free building blocks.
- Decision: CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting (DI, configuration, logging), xUnit,
  Shouldly. No commercially licensed libraries (FluentAssertions 8+ excluded).
- Consequences: one composition style across UI, worker and CLI.
