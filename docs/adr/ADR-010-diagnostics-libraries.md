# ADR-010 Diagnostics libraries

- Context: reading dumps and talking to runtimes is complex; Microsoft's libraries are the reference.
- Decision: ClrMD, Microsoft.Diagnostics.NETCore.Client, TraceEvent, latest stable at sprint 0.1.
- Consequences: versions verified at 0.1 (section 14); upgrades are deliberate.
- Amendment (sprint 1.8): TraceEvent ships no gcdump reader or writer; that code exists only as MIT source in PerfView
  and dotnet-gcdump. HeapScope vendors dotnet-gcdump's copy (`src/HeapScope.ThirdParty.GcDump`, dotnet/diagnostics
  `b1a4548`, from PerfView `76dc28af`) with the changes listed in its `CHANGES.md` (four by sprint 1.9), listed
  in `THIRD-PARTY-NOTICES.md`. An update replaces the files from a newer commit and reapplies the listed changes.
