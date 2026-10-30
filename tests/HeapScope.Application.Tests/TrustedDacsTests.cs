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

        const string Sha = "039058c6f2c0cb492c533b0a4d14ef77cc0f78abccced5287d84a1a2011cfb81";

        trusted.TrustFile(dac, new string('e', 64), "a copy that differs from its source").ShouldBeFalse("only the content copied is trusted");
        trusted.All().ShouldBeEmpty();
        trusted.TrustFile(dac, Sha, "copied by HeapScope's capture").ShouldBeTrue();

        trusted.All().ShouldBe([Sha]);
        TrustedDacs.Sha256Of(dac).ShouldBe(Sha);
        trusted.TrustFile(Path.Combine(_folder, "missing.dll"), Sha, "x").ShouldBeFalse();
    }

    [Fact]
    public void A_file_this_build_cannot_read_trusts_nothing_and_is_kept_aside_before_a_new_trust()
    {
        // Sprint 3.6 review: a newer build's list or a damaged one is never overwritten silently.
        Directory.CreateDirectory(_folder);
        var file = Path.Combine(_folder, TrustedDacs.FileName);
        const string Newer = """{ "FormatVersion": 2, "Dacs": [] }""";
        File.WriteAllText(file, Newer);
        var trusted = new TrustedDacs(_folder);

        trusted.All().ShouldBeEmpty();
        trusted.Trust(new string('f', 64), "C:/dumps/mscordaccore.dll", "trusted by the user");

        trusted.All().ShouldBe([new string('f', 64)]);
        File.ReadAllText(Directory.EnumerateFiles(_folder, TrustedDacs.FileName + ".*.bak").ShouldHaveSingleItem()).ShouldBe(Newer);
    }

    [Fact]
    public void Two_HeapScope_processes_never_lose_each_others_trust()
    {
        // Two instances stand for two processes: the lock file and the shared reads are across processes (review).
        var first = new TrustedDacs(_folder);
        var second = new TrustedDacs(_folder);

        Parallel.For(0, 40, i => (i % 2 == 0 ? first : second).Trust(i.ToString("x64", System.Globalization.CultureInfo.InvariantCulture), "x", "test"));

        new TrustedDacs(_folder).All().Count.ShouldBe(40);
        Directory.EnumerateFiles(_folder, "*.tmp").ShouldBeEmpty();
    }

    [Fact]
    public void The_open_log_says_who_trusted_each_DAC_and_when()
    {
        // Sprint 3.6 review H2: the worker names the hash it loaded; this line, under the same trace id, says why.
        var trusted = new TrustedDacs(_folder);
        trusted.Describe().ShouldBe("none");
        trusted.Trust(new string('a', 64), "C:/dumps/mscordaccore.dll", "trusted by the user when opening C:/dumps/app.dmp");
        trusted.TrustForThisRun(new string('b', 64));

        var described = trusted.Describe();

        described.ShouldContain($"{new string('a', 64)} at C:/dumps/mscordaccore.dll, trusted by the user when opening C:/dumps/app.dmp, since ");
        described.ShouldContain($"{new string('b', 64)} for this run (--trust-dac)");
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
