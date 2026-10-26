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


namespace HeapScope.Loaders.ClrMd.Tests;

/// <summary>
/// Sprint 2.3.5 review H1: on Linux and macOS the mount that holds a path decides whether it is local (ADR-017). The mount
/// table and the links are given, so these run on every system.
/// </summary>
public sealed class UnixLocalPathTests
{
    private static readonly MountPoint[] s_mounts =
    [
        new("/", DriveType.Fixed, "overlay"),
        new("/tmp", DriveType.Ram, "tmpfs"),
        new("/proc", DriveType.Ram, "proc"),
        new("/dev", DriveType.Ram, "devtmpfs"),
        new("/mnt/share", DriveType.Network, "nfs"),
        new("/net", DriveType.Network, "autofs"),
        new("/home/me/remote", DriveType.Unknown, "fuse.sshfs"),
    ];

    [Theory]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.14/libmscordaccore.so", true)]
    [InlineData("/tmp/dumps/core.1234", true)]
    [InlineData("/mnt/share/dumps/core.1234", false)]
    [InlineData("/mnt/share", false)]
    [InlineData("/mnt/shared/core.1234", true)]
    [InlineData("/net/host/dac/libmscordaccore.so", false)]
    [InlineData("/home/me/remote/libmscordaccore.so", false)]
    [InlineData("/proc/self/fd/0", false)]
    [InlineData("/dev/zero", false)]
    [InlineData("/opt/app/../../mnt/share/x", false)]
    [InlineData("relative/libmscordaccore.so", false)]
    [InlineData("", false)]
    public void A_path_is_local_when_its_own_mount_is_a_local_disk(string path, bool expected) =>
        UnixLocalPath.IsLocal(path, s_mounts, _ => null).ShouldBe(expected);

    [Fact]
    public void Links_are_followed_to_where_they_point()
    {
        var links = new Dictionary<string, string>
        {
            ["/opt/dac"] = "/mnt/share/dac",
            ["/opt/local"] = "../usr/share/dotnet",
            ["/opt/loop"] = "/opt/loop",
        };
        string? Link(string path) => links.GetValueOrDefault(path);

        UnixLocalPath.IsLocal("/opt/dac/libmscordaccore.so", s_mounts, Link).ShouldBeFalse("the link points to a network share");
        UnixLocalPath.IsLocal("/opt/local/shared", s_mounts, Link).ShouldBeTrue();
        UnixLocalPath.IsLocal("/opt/loop/x", s_mounts, Link).ShouldBeFalse("a link loop is refused");
    }

    [Fact]
    public void Nothing_on_a_network_mount_is_looked_at()
    {
        // An automount fires when a path on it is looked at, so the mount is checked before the link is read.
        var looked = new List<string>();
        UnixLocalPath.IsLocal("/net/host/share/x", s_mounts, path =>
        {
            looked.Add(path);
            return null;
        }).ShouldBeFalse();

        looked.ShouldBeEmpty();
    }

    [Fact]
    public void A_path_under_no_known_mount_is_refused() =>
        UnixLocalPath.IsLocal("/data/x", [new MountPoint("/data2", DriveType.Fixed, "ext4")], _ => null).ShouldBeFalse();

    [Fact]
    public void This_systems_temp_folder_is_local_off_windows()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix rule reads this system's mounts.");

        DacLocator.IsLocalFixedPath(Path.GetTempPath()).ShouldBeTrue();
        if (OperatingSystem.IsLinux())
        {
            DacLocator.IsLocalFixedPath("/proc/self/status").ShouldBeFalse();
        }
    }
}
