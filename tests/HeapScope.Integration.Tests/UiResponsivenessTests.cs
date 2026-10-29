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

using HeapScope.Integration.Tests.Infrastructure;

namespace HeapScope.Integration.Tests;

/// <summary>Runs alone: responsiveness measurements are meaningless while other tests saturate the CPU.</summary>
[CollectionDefinition(nameof(UiResponsivenessCollection), DisableParallelization = true)]
public sealed class UiResponsivenessCollection;

/// <summary>Sprint 0.3: no dispatcher block over 100 ms while a 5M synthetic job runs (decision 7).</summary>
[Collection(nameof(UiResponsivenessCollection))]
[Trait("Category", "Integration")]
[Trait("Category", "UI")]
public sealed class UiResponsivenessTests : IDisposable
{
    private readonly string _logDirectory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Ui_stays_responsive_during_a_5M_synthetic_job()
    {
        using var ui = ChildProcess.Start(TestPaths.UiExe, "--smoke-synthetic", "5000000", "--Logging:File:Directory", _logDirectory);

        (await ui.WaitForExitAsync(TimeSpan.FromSeconds(120))).ShouldBeTrue();
        var log = string.Join(Environment.NewLine, Directory.GetFiles(_logDirectory, "*.log").Select(File.ReadAllText));
        ui.Process.ExitCode.ShouldBe(0, log);
        log.ShouldContain("Smoke run: job completed True");

        // Sprint 2.3.6: the cold start is measured in every smoke run (the UI cold start backlog item).
        log.ShouldContain("First window rendered");
    }

    public void Dispose()
    {
        if (Directory.Exists(_logDirectory))
        {
            Directory.Delete(_logDirectory, recursive: true);
        }
    }
}
