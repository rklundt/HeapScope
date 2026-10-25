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

namespace HeapScope.Contracts;

// DTOs are records of primitives, strings, arrays and Contracts enums only (ARCHITECTURE.md 4.4, ADR-016).
// Retention paths (sprint 2.2): why an object or the objects of a type are alive.

/// <summary>A GC root's kind on the wire (mirrors Core's <c>RootKind</c>).</summary>
public enum RootKindDto
{
    Other = 0,
    StaticField = 1,
    ThreadStack = 2,
    StrongHandle = 3,
    PinnedHandle = 4,
    AsyncPinnedHandle = 5,
    RefCountedHandle = 6,
    FinalizerQueue = 7,
    WeakHandle = 8,
}

/// <summary>
/// Where a retention path starts: a retaining root, or a frozen object (<see cref="Frozen"/>, never collected; its
/// <see cref="Kind"/> is then <see cref="RootKindDto.Other"/>). <see cref="Description"/> says it in plain language.
/// </summary>
public sealed record PathRootDto(RootKindDto Kind, bool Frozen, string Description);

/// <summary>
/// One object of an object's retention tree (sprint 2.2). Node 0 is the inspected object; every other node references
/// the node <see cref="ChildId"/> on its shortest way there, through <see cref="Field"/> (full dumps) or a dependent handle
/// (<see cref="ViaDependentHandle"/>). <see cref="Root"/> is set on the nodes where paths start; <see cref="RootsBelow"/>
/// counts the roots found whose paths pass through the node. <see cref="IsFramework"/> marks a step of a framework type
/// (a <c>System.</c> or <c>Microsoft.</c> namespace) between other steps, which front ends may fold. <see cref="Folded"/>
/// counts the objects of the same type behind this one on the path (a list folded into one node, review H1); a gap node,
/// which stands for the middle of a long path, has <see cref="Address"/> 0 and <see cref="Folded"/> objects.
/// </summary>
public sealed record RetentionNodeDto(
    int Id,
    int ChildId,
    ulong Address,
    string TypeName,
    string? Field,
    bool ViaDependentHandle,
    bool IsFramework,
    int Depth,
    PathRootDto? Root,
    int RootsBelow,
    int Folded);

/// <summary>A step of a key path: a type, and whether it is a framework type between other steps.</summary>
public sealed record PathStepDto(string TypeName, bool IsFramework);

/// <summary>
/// A key retention path: the type sequence from a root to the objects (repeats of one type folded), with how many objects
/// it explains (for an object's paths: how many of the roots found share it) and their bytes, and one example object.
/// <see cref="Steps"/> is empty when the objects are roots themselves.
/// </summary>
public sealed record KeyPathDto(PathRootDto Root, IReadOnlyList<PathStepDto> Steps, string TargetType, long Objects, long Bytes, ulong ExampleAddress);

/// <summary>Why one object is alive: its shortest paths from up to <paramref name="MaxRoots"/> roots (1 to 50).</summary>
public sealed record ObjectPathsRequest(Guid SnapshotId, ulong Address, int MaxRoots = 10);

/// <summary>
/// The answer to an object paths query. <see cref="Reachable"/> is false when no root holds the object (garbage not yet
/// collected, or held only weakly). <see cref="StoppedEarly"/>: the search hit its limits, so more roots may exist.
/// <see cref="Truncated"/>: roots were found whose paths would pass the tree's node limit and were left out.
/// <see cref="UnreachableNote"/> says what an unreachable object means for this kind of snapshot (review M3): garbage not
/// yet collected in a full dump, a root the gcdump did not record in a gcdump, which holds only survivors.
/// </summary>
public sealed record ObjectPathsResult(
    IReadOnlyList<RetentionNodeDto> Nodes,
    IReadOnlyList<int> RootNodeIds,
    IReadOnlyList<KeyPathDto> KeyPaths,
    bool Reachable,
    bool StoppedEarly,
    int Visited,
    string? FieldsNote,
    bool CompleteGraph,
    ReverseIndexInfo Index,
    bool Truncated,
    string UnreachableNote);

/// <summary>Why the objects of a type are alive: their key paths, the largest <paramref name="MaxPaths"/> (1 to 100).</summary>
public sealed record TypePathsRequest(Guid SnapshotId, string TypeName, string? Module, int MaxPaths = 20);

/// <summary>
/// The answer to a type paths query: the key paths, largest first; the objects no root reaches; the paths beyond the limit
/// summed. <see cref="TreeSeconds"/> is the time this query spent building the snapshot's root path tree (0 when built).
/// <see cref="UnreachableNote"/> as in <see cref="ObjectPathsResult"/>.
/// </summary>
public sealed record TypePathsResult(
    IReadOnlyList<KeyPathDto> Paths,
    int MatchedTypes,
    long Objects,
    long Unreachable,
    long UnreachableBytes,
    int OtherPaths,
    long OtherObjects,
    long OtherBytes,
    bool CompleteGraph,
    double TreeSeconds,
    string UnreachableNote);
