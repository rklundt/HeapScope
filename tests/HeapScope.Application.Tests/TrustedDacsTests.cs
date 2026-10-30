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

using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

public sealed class TrustedDacsTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void A_trusted_DAC_is_kept_by_its_hash_and_a_run_only_trust_is_not_written()
    {
        // Sprint 3.6, peer review P2.
        var trusted = new TrustedDacs(_folder);
        trusted.All().ShouldBeEmpty();

        trusted.Trust(new string('b', 64), @"C:\dumps\mscordaccore.dll", "trusted by the user");
        trusted.Trust(new string('b', 64), @"C:\elsewhere\mscordaccore.dll", "again");
        trusted.TrustForThisRun(new string('C', 64));

        trusted.All().ShouldBe([new string('b', 64), new string('c', 64)], "the CLI's hash is normalized to lower case");
        new TrustedDacs(_folder).All().ShouldBe([new string('b', 64)], "only the kept one survives the run");
        trusted.Kept().ShouldHaveSingleItem().Path.ShouldBe(@"C:\dumps\mscordaccore.dll", "the first trust is kept");
    }

    [Fact]
    public void A_capture_trusts_the_DAC_it_copied_by_its_content()
    {
        Directory.CreateDirectory(_folder);
        var dac = Path.Combine(_folder, "mscordaccore.dll");
        File.WriteAllBytes(dac, [1, 2, 3]);
        var trusted = new TrustedDacs(_folder);

        trusted.TrustFile(dac, "copied by HeapScope's capture").ShouldBeTrue();

        trusted.All().ShouldBe([TrustedDacs.Sha256Of(dac)]);
        TrustedDacs.Sha256Of(dac).ShouldBe("039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81");
        trusted.TrustFile(Path.Combine(_folder, "missing.dll"), "x").ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Anything_but_a_SHA_256_is_refused(string value)
    {
        var trusted = new TrustedDacs(_folder);
        Should.Throw<ArgumentException>(() => trusted.TrustForThisRun(value));
        Should.Throw<ArgumentException>(() => trusted.Trust(value, "x", "y"));
    }

    [Fact]
    public void An_unreadable_file_trusts_nothing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, TrustedDacs.FileName), "{ not json");

        new TrustedDacs(_folder).All().ShouldBeEmpty("the user is asked again rather than trusting a damaged list");
    }

    [Fact]
    public void A_refusal_carries_the_DAC_to_trust()
    {
        // The JSON-RPC formatter hands the worker's error data over as its own JSON object.
        var sha = new string('d', 64);
        WorkerHost.DacTrustOf($$"""{ "Path": "C:/dumps/mscordaccore.dll", "Sha256": "{{sha}}", "RuntimeVersion": "10.0.13" }""")
            .ShouldBe(new DacTrustDetails("C:/dumps/mscordaccore.dll", sha, "10.0.13"));
        WorkerHost.DacTrustOf(null).ShouldBeNull();
        WorkerHost.DacTrustOf("""{ "Path": "x", "Sha256": "short" }""").ShouldBeNull();
        WorkerHost.DacTrustOf("not json").ShouldBeNull();
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
