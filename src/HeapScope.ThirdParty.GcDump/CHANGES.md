# Vendored gcdump code: origin and changes

These files are MIT licensed (`LICENSE.TXT`, copyright .NET Foundation and Contributors) and keep their original
headers. They are part of HeapScope's build but not of HeapScope's own code: HeapScope's license and additional terms
do not apply to them (`THIRD-PARTY-NOTICES.md`).

## Origin

- Copied on 2026-10-24 from https://github.com/dotnet/diagnostics, folder `src/Tools/dotnet-gcdump/DotNetHeapDump`,
  commit `b1a45488914403094e274adb33f103f273a9d69c`: `DotNetHeapDumpGraphReader.cs`, `DotNetHeapInfo.cs`,
  `EventPipeDotNetHeapDumper.cs`, `GCHeapDump.cs`, `Graph.cs`, `MemoryGraph.cs`, `README.md`.
- That folder copies the files from https://github.com/microsoft/perfview at commit
  `76dc28af873e27aa8c4f9ce8efa0971a2c738165` (see `README.md`, which is theirs and unchanged).

## Changes made by HeapScope

Each change is marked with a `HeapScope change` comment.

1. `EventPipeDotNetHeapDumper.cs`, `EventPipeSessionController`: sessions connect by process id only. The diagnostic
   port and dsrouter path used `IpcEndpointConfig` and `PidIpcEndpoint`, which are internal to the published
   `Microsoft.Diagnostics.NETCore.Client` package; passing a diagnostic port now throws `ArgumentException`, and
   `UseWildcardProcessId` is always false.
2. `EventPipeDotNetHeapDumper.cs`, `EventPipeSessionController`: `EventPipeSession.DefaultRundownKeyword` is internal to
   the published package, so its value (`0x80020139`) is a local constant.
3. `EventPipeDotNetHeapDumper.cs`, `DumpFromEventPipe`: the task that ends the session runs with
   `CancellationToken.None` instead of the capture's token. With the canceled token it never ran, so the event reader
   waited for the session forever and a canceled capture never returned (sprint 1.8 review H1).
4. `EventPipeDotNetHeapDumper.cs`, `EventPipeSessionController`: the warning about lossy buffering (printed on every
   .NET 10 capture) is written to the dump's log, which HeapScope sends to its own log, instead of the console
   (sprint 1.9). The controller takes the log as an optional parameter; without one it still writes to the console.
5. `GCHeapDump.cs` and `Graph.cs`: counts read from the file (count multipliers, COM records, types, nodes, blob bytes)
   are checked against the stream's length before anything is allocated for them (`GCHeapDump.CheckedCount`, a
   thread-static limit set by the stream constructor); a count no file of that length can hold fails with
   `InvalidDataException` (sprint 3.7, peer review P13: a crafted file made the reader allocate gigabytes).
