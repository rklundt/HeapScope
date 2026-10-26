# Third-Party Notices

HeapScope (Copyright (C) 2026 Ray Klundt, licensed under the GNU AGPL v3 only with additional terms; see `LICENSE`,
`ADDITIONAL-TERMS.md` and `COMMERCIAL.md`) uses the third-party components below. Each keeps its own license, and its
notices must be preserved under any license of HeapScope, including a commercial one.

## Vendored source code

Vendored code lives in a `ThirdParty` project or folder, keeps its original license header, and is listed here with its
origin (repository, path and commit) and license.

### gcdump reader and writer (`src/HeapScope.ThirdParty.GcDump`)

- Files: `DotNetHeapDumpGraphReader.cs`, `DotNetHeapInfo.cs`, `EventPipeDotNetHeapDumper.cs`, `GCHeapDump.cs`,
  `Graph.cs`, `MemoryGraph.cs`, `README.md`.
- Origin: https://github.com/dotnet/diagnostics, `src/Tools/dotnet-gcdump/DotNetHeapDump`, commit
  `b1a45488914403094e274adb33f103f273a9d69c`, which copies them from https://github.com/microsoft/perfview at commit
  `76dc28af873e27aa8c4f9ce8efa0971a2c738165`.
- License: MIT, Copyright (c) .NET Foundation and Contributors (text below and in the project's `LICENSE.TXT`).
- Changes: listed in the project's `CHANGES.md`.

## NuGet packages shipped with HeapScope

These are referenced as packages (not copied into this repository) and are deployed with the UI, CLI or worker. All are
under the MIT License, whose text is below.

| Package | Version | Copyright |
|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Headless and their platform packages (Avalonia.Skia, Avalonia.HarfBuzz, Avalonia.Win32, Avalonia.X11, Avalonia.FreeDesktop, Avalonia.FreeDesktop.AtSpi, Avalonia.Native, Avalonia.Remote.Protocol, Avalonia.Fonts.Inter) | 12.1.4 | The AvaloniaUI Project (Avalonia.Fonts.Inter embeds the Inter typeface, under the SIL Open Font License 1.1) |
| Avalonia.Controls.DataGrid | 12.1.2 | The AvaloniaUI Project |
| Azure.Core (via ClrMD) | 1.53.0 | Microsoft Corporation |
| Azure.Identity (via ClrMD) | 1.21.0 | Microsoft Corporation |
| CommunityToolkit.Mvvm | 8.4.2 | .NET Foundation and Contributors |
| MessagePack, MessagePack.Annotations (via StreamJsonRpc) | 2.5.307 | Yoshifumi Kawai and contributors |
| Microsoft.Bcl.AsyncInterfaces | 10.0.3 | Microsoft Corporation |
| Microsoft.Diagnostics.NETCore.Client | 0.2.750501 (0.2.661903 via TraceEvent) | Microsoft Corporation |
| Microsoft.Diagnostics.Runtime (ClrMD) | 4.2.751201 | Microsoft Corporation |
| Microsoft.Diagnostics.Tracing.TraceEvent | 4.0.0 | Microsoft Corporation (see the note on native binaries below) |
| Microsoft.Extensions.* (Hosting, Configuration, DependencyInjection, Logging, Options, Primitives, FileProviders, FileSystemGlobbing, Diagnostics) | 10.0.13 | Microsoft Corporation |
| Microsoft.Identity.Client, Microsoft.Identity.Client.Extensions.Msal (via ClrMD) | 4.83.1 | Microsoft Corporation |
| Microsoft.IdentityModel.Abstractions (via ClrMD) | 8.14.0 | Microsoft Corporation |
| Microsoft.NET.StringTools | 17.6.3 | Microsoft Corporation |
| Microsoft.Security.AntiSSRF (via ClrMD) | 1.0.0 | Microsoft Corporation |
| MicroCom.Runtime (via Avalonia) | 0.11.6 | Nikita Tsukanov |
| Microsoft.VisualStudio.Threading.Only, Microsoft.VisualStudio.Validation (via StreamJsonRpc) | 18.7.23, 17.13.22 | Microsoft Corporation |
| Nerdbank.MessagePack, Nerdbank.Streams (via StreamJsonRpc) | 1.3.88, 2.13.16 | Andrew Arnott |
| Newtonsoft.Json (via StreamJsonRpc) | 13.0.4 | James Newton-King |
| PolyType (via StreamJsonRpc) | 1.4.1 | Eirik Tsarpalis |
| SkiaSharp, HarfBuzzSharp and their native assets (via Avalonia) | 3.119.4, 8.3.1.3 | Microsoft Corporation |
| StreamJsonRpc | 2.26.150 | Microsoft Corporation |
| System.ClientModel, System.Memory.Data (via ClrMD) | 1.10.0, 10.0.3 | Microsoft Corporation |
| System.CommandLine | 2.0.13 | Microsoft Corporation |
| System.Diagnostics.EventLog | 10.0.13 | Microsoft Corporation |
| System.IO.Hashing | 10.0.13 | Microsoft Corporation |
| System.Reflection.TypeExtensions | 4.7.0 | Microsoft Corporation |
| System.Security.Cryptography.ProtectedData (via ClrMD) | 4.5.0 | Microsoft Corporation |
| Tmds.DBus.Protocol (via Avalonia, Linux) | 0.94.1 | Tom Deseyn |

### ANGLE (Avalonia.Angle.Windows.Natives, 2.1.27548.20260419, Windows only)

Avalonia renders through ANGLE on Windows. Its native binaries are under the BSD 3-Clause License:

> Copyright 2018 The ANGLE Project Authors. All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted provided that the
> following conditions are met: Redistributions of source code must retain the above copyright notice, this list of
> conditions and the following disclaimer. Redistributions in binary form must reproduce the above copyright notice,
> this list of conditions and the following disclaimer in the documentation and/or other materials provided with the
> distribution. Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc. Ltd., nor the names of their
> contributors may be used to endorse or promote products derived from this software without specific prior written
> permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES,
> INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
> DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL,
> SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR
> SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
> WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE
> OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

### Native binaries inside Microsoft.Diagnostics.Tracing.TraceEvent

The TraceEvent package copies two native Microsoft binaries into the build output (`amd64/KernelTraceControl.dll` and
`amd64/msdia140.dll`). They ship without source code. The package as a whole is declared MIT, but these binaries come
from Microsoft's Windows Performance Toolkit and the Debug Interface Access SDK and may carry their own
redistribution terms. They are kept on purpose (product owner, 2026-10-24) for future kernel tracing and symbol
lookup; review their redistribution terms before HeapScope is distributed.

## Development and test only (not shipped)

| Package | Version | License | Copyright |
|---|---|---|---|
| xunit.v3 | 4.0.2 | Apache-2.0 | .NET Foundation |
| Shouldly | 4.3.0 | BSD-3-Clause | Jake Ginnivan, Joseph Woodward, Simon Cropp and contributors |
| Microsoft.CodeAnalysis.CSharp (DenseHeap fixture) | 5.0.0 | MIT | Microsoft Corporation |
| Avalonia.BuildServices (build time, via Avalonia; its telemetry task is disabled by `Directory.Build.targets`) | 11.3.2 | MIT | The AvaloniaUI Project |

## MIT License

Applies to the vendored gcdump code and to every package marked MIT above, with the copyright line given for it.

```
Copyright (c) <copyright holders listed above>

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
