// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

using System.Security.Cryptography;
using HeapScope.Core.Loading;

namespace HeapScope.Loaders.ClrMd.Tests;

/// <summary>
/// The trust of a DAC found next to a dump (ADR-035) on a real dump of any system (sprint 3.7, 3.6 review M19): Linux and
/// macOS have no signature check behind it, so it must hold there. Runs when <c>HEAPSCOPE_FIXTURE_DUMP</c> names a dump of
/// the LeakyConsole fixture (CI's Linux and macOS jobs set it after taking the core dump); skipped otherwise. The dump's
/// folder must be writable: the DAC is planted there and removed after.
/// </summary>
public sealed class PlantedDacTests : IDisposable
{
    private readonly string _work = Directory.CreateTempSubdirectory("heapscope-planted-").FullName;

    [Fact]
    public void A_DAC_found_only_next_to_a_real_dump_is_refused_until_trusted_then_loaded_from_a_private_copy()
    {
        var dump = Environment.GetEnvironmentVariable("HEAPSCOPE_FIXTURE_DUMP");
        Assert.SkipWhen(string.IsNullOrEmpty(dump) || !File.Exists(dump), "HEAPSCOPE_FIXTURE_DUMP names no dump (set by CI after it takes one).");

        DacChoice? installed = null;
        new ClrMdDumpLoader(new ClrMdLoaderOptions { DacChosen = c => installed = c }).Load(dump!, cancellationToken: TestContext.Current.CancellationToken).Graph.Dispose();
        var runtime = Path.GetDirectoryName(installed?.Path);
        Assert.SkipWhen(runtime is null, "The dumped runtime's DAC was not found on this machine.");
        string[] names = OperatingSystem.IsWindows() ? ["mscordaccore.dll"]
            : OperatingSystem.IsMacOS() ? ["libmscordaccore.dylib", "libcoreclr.dylib"]
            : ["libmscordaccore.so", "libcoreclr.so"];
        var folder = Path.GetDirectoryName(Path.GetFullPath(dump!))!;
        var planted = names.Select(n => Path.Combine(folder, n)).ToArray();
        var noRuntimes = Directory.CreateDirectory(Path.Combine(_work, "no-runtimes")).FullName;
        var copies = Path.Combine(_work, "copies");
        try
        {
            foreach (var name in names)
            {
                File.Copy(Path.Combine(runtime!, name), Path.Combine(folder, name), overwrite: true);
            }

            var untrusted = new ClrMdLoaderOptions { InstalledRuntimeRoots = [noRuntimes], SearchDumpedRuntimeDirectory = false, OwnRuntimeDirectory = null };
            var refused = Should.Throw<UntrustedDacException>(() => new ClrMdDumpLoader(untrusted).Load(dump!, cancellationToken: TestContext.Current.CancellationToken));
            refused.Sha256.ShouldBe(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(planted[0]))));
            Should.Throw<UntrustedDacException>(() => ClrMdDumpLoader.CheckDacTrust(dump!, untrusted));

            DacChoice? chosen = null;
            var trusted = untrusted with { IsTrustedDac = s => s == refused.Sha256, TrustedDacCopyFolder = copies, DacChosen = c => chosen = c };
            var loaded = new ClrMdDumpLoader(trusted).Load(dump!, cancellationToken: TestContext.Current.CancellationToken);
            loaded.Graph.ObjectCount.ShouldBeGreaterThan(1000);
            loaded.Graph.Dispose();
            chosen.ShouldNotBeNull().Path.ShouldBe(Path.Combine(copies, refused.Sha256 ?? "", names[0]), "the private copy is loaded, not the planted file");
        }
        finally
        {
            foreach (var file in planted)
            {
                File.Delete(file);
            }
        }
    }

    public void Dispose() => Directory.Delete(_work, recursive: true);
}
