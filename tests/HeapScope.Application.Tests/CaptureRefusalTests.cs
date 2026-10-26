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


using HeapScope.Application.Jobs;
using HeapScope.Application.Sessions;
using HeapScope.Capture;

namespace HeapScope.Application.Tests;

/// <summary>Sprint 2.3.5 review: capture refused off Windows reaches front ends as a capture failure with its own reason.</summary>
public sealed class CaptureRefusalTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("heapscope-refusal-").FullName;

    [Fact]
    public async Task Listing_and_capturing_where_capture_is_not_supported_fail_with_that_reason()
    {
        var service = new CaptureService(new RefusingDiscovery(), new DumpCapturer(), new SessionLibrary(_root), new JobTracker(TimeProvider.System));

        var list = await Should.ThrowAsync<CaptureFailedException>(() => service.ListProcessesAsync(CancellationToken.None));
        var capture = await Should.ThrowAsync<CaptureFailedException>(() => service.StartCapture(42).Result);

        list.Reason.ShouldBe(CaptureFailure.NotSupportedOnThisSystem);
        capture.Reason.ShouldBe(CaptureFailure.NotSupportedOnThisSystem);
        capture.Message.ShouldContain("dotnet-dump");
        Directory.EnumerateFileSystemEntries(_root).ShouldBeEmpty("a refused capture leaves no session");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class RefusingDiscovery : IProcessDiscovery
    {
        public IReadOnlyList<DotNetProcess> List(CancellationToken cancellationToken = default) => throw Refusal();

        public DotNetProcess? Find(int processId) => throw Refusal();

        private static PlatformNotSupportedException Refusal()
        {
            try
            {
                CapturePlatform.EnsureSupported(isWindows: false);
            }
            catch (PlatformNotSupportedException ex)
            {
                return ex;
            }

            throw new InvalidOperationException("EnsureSupported(false) did not refuse.");
        }
    }
}
