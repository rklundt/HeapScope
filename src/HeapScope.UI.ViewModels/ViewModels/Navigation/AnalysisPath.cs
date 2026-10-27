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

using System.Globalization;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// A step of an <see cref="AnalysisPath"/> (sprint 2.7): a subject of the Snapshot tab (the types grid, instances, an
/// object, a type's referrers or paths), or a link into the Retainers tab, replayed when the step is chosen again.
/// </summary>
public sealed class AnalysisStep
{
    private readonly List<AnalysisStep> _children = [];

    internal AnalysisStep(AnalysisStep? parent, INavigationSubject? subject, NavigationTarget? target)
    {
        Parent = parent;
        Subject = subject;
        Target = target;
    }

    public AnalysisStep? Parent { get; private set; }

    /// <summary>The step's number on its path, in the order opened (the root is 0): the log names steps by it.</summary>
    public int Id { get; internal set; }

    public IReadOnlyList<AnalysisStep> Children => _children;

    /// <summary>The Snapshot tab's subject; null for a Retainers step.</summary>
    public INavigationSubject? Subject { get; }

    /// <summary>The Retainers tab's link; null for a Snapshot tab step.</summary>
    public NavigationTarget? Target { get; }

    /// <summary>Steps above this one; the sidebar indents by it.</summary>
    public int Depth => Parent is null ? 0 : Parent.Depth + 1;

    /// <summary>The child chosen last: where Forward goes.</summary>
    public AnalysisStep? LastChild { get; internal set; }

    /// <summary>When it was last shown (a counter): the cap closes the least recently shown leaf first.</summary>
    internal long Shown { get; set; }

    internal bool IsClosed { get; private set; }

    public string Title => Subject?.Title ?? TargetTitle(Target!);

    /// <summary>The Snapshot tab's subject shown while this step is current: its own, or its nearest ancestor's.</summary>
    public INavigationSubject SubjectShown => Subject ?? Parent!.SubjectShown;

    internal void Add(AnalysisStep child)
    {
        child.Parent = this;
        _children.Add(child);
    }

    internal void Remove(AnalysisStep child)
    {
        _children.Remove(child);
        if (LastChild == child)
        {
            LastChild = _children.Count > 0 ? _children[^1] : null;
        }
    }

    /// <summary>Takes this step out from between its parent and its children, which move up to the parent.</summary>
    internal void Splice()
    {
        var parent = Parent!;
        parent.Remove(this);
        foreach (var child in _children)
        {
            parent.Add(child);
        }

        _children.Clear();
        LastChild = null;
        IsClosed = true;
        Subject?.Close();
    }

    /// <summary>Closes this step and every step below it, releasing their subjects' result sets.</summary>
    internal void CloseBranch()
    {
        foreach (var child in _children)
        {
            child.CloseBranch();
        }

        _children.Clear();
        LastChild = null;
        IsClosed = true;
        Subject?.Close();
    }

    private static string TargetTitle(NavigationTarget target) => target.Kind switch
    {
        NavigationKind.Retainers when target.Address != 0 => string.Create(CultureInfo.InvariantCulture, $"Retainers of 0x{target.Address:x}"),
        NavigationKind.Retainers when target.TypeName is { } type => "Retainers: " + type,
        NavigationKind.Retainers => "Biggest retainers",
        NavigationKind.RetainedBySet => "Retained by the set of " + target.TypeName,
        _ => target.Kind.ToString(),
    };
}

/// <summary>
/// One snapshot's analysis path (sprint 2.7, dotMemory's Analysis Path): a tree of the steps taken from the types grid.
/// Opening something adds a child of the current step, so opening from an earlier step starts a branch beside the later
/// steps instead of dropping them. Back goes to the parent; Forward to the child shown last. At most
/// <see cref="MaxSteps"/> steps are kept: past that, the least recently shown leaf off the current trail is closed, or,
/// when the trail alone is longer, its oldest step below the root (what came after it moves up).
/// </summary>
public sealed class AnalysisPath
{
    /// <summary>Steps kept, the root included.</summary>
    public const int MaxSteps = 30;

    private long _clock;
    private int _nextId = 1;

    /// <param name="root">The types grid: the root step, never closed by the path (the grid is shared by every snapshot).</param>
    public AnalysisPath(INavigationSubject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        Root = new AnalysisStep(null, root, null);
        Current = Root;
        Count = 1;
    }

    public AnalysisStep Root { get; }

    public AnalysisStep Current { get; private set; }

    /// <summary>Steps on the path, the root included.</summary>
    public int Count { get; private set; }

    public bool CanGoBack => Current.Parent is not null;

    public bool CanGoForward => Current.LastChild is not null;

    /// <summary>The steps from the root to the current one (the breadcrumb).</summary>
    public IReadOnlyList<AnalysisStep> Trail
    {
        get
        {
            var trail = new List<AnalysisStep>();
            for (var step = Current; step is not null; step = step.Parent)
            {
                trail.Add(step);
            }

            trail.Reverse();
            return trail;
        }
    }

    /// <summary>Every step, each followed by its children (the sidebar's order).</summary>
    public IReadOnlyList<AnalysisStep> Steps
    {
        get
        {
            var steps = new List<AnalysisStep>(Count);
            Walk(Root);
            return steps;

            void Walk(AnalysisStep step)
            {
                steps.Add(step);
                foreach (var child in step.Children)
                {
                    Walk(child);
                }
            }
        }
    }

    /// <summary>Opens <paramref name="subject"/> as a child of the current step and makes it current.</summary>
    public AnalysisStep Open(INavigationSubject subject) => Add(new AnalysisStep(Current, subject ?? throw new ArgumentNullException(nameof(subject)), null));

    /// <summary>Records a link into the Retainers tab as a child of the current step and makes it current.</summary>
    public AnalysisStep Open(NavigationTarget target) => Add(new AnalysisStep(Current, null, target ?? throw new ArgumentNullException(nameof(target))));

    public AnalysisStep Back() => Select(Current.Parent ?? Current);

    public AnalysisStep Forward() => Select(Current.LastChild ?? Current);

    /// <summary>Makes <paramref name="step"/> current; each step above it remembers the way down for Forward.</summary>
    public AnalysisStep Select(AnalysisStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.IsClosed || !Contains(step))
        {
            throw new ArgumentException("The step is not on this path.", nameof(step));
        }

        Current = step;
        step.Shown = ++_clock;
        for (var child = step; child.Parent is { } parent; child = parent)
        {
            parent.LastChild = child;
        }

        return step;
    }

    /// <summary>
    /// Closes <paramref name="step"/> and its branch (not the root); when the current step was in it, its parent becomes
    /// current. Returns the steps closed.
    /// </summary>
    public int Close(AnalysisStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        if (step.Parent is not { } parent || step.IsClosed)
        {
            return 0;
        }

        var inBranch = Trail.Contains(step);
        var closed = CountBranch(step);
        parent.Remove(step);
        step.CloseBranch();
        Count -= closed;
        if (inBranch)
        {
            Select(parent);
        }

        return closed;
    }

    /// <summary>Closes every step but the root (the snapshot was closed).</summary>
    public void CloseAll()
    {
        foreach (var child in Root.Children.ToList())
        {
            Close(child);
        }
    }

    private AnalysisStep Add(AnalysisStep step)
    {
        step.Id = _nextId++;
        step.Parent!.Add(step);
        Count++;
        Select(step);
        while (Count > MaxSteps)
        {
            if (OldestLeafOffTrail() is { } oldest)
            {
                Close(oldest);
            }
            else
            {
                Trail[1].Splice();
                Count--;
                Select(Current);
            }
        }

        return step;
    }

    private AnalysisStep? OldestLeafOffTrail()
    {
        var trail = Trail.ToHashSet();
        return Steps.Where(s => s.Children.Count == 0 && !trail.Contains(s)).MinBy(s => s.Shown);
    }

    private bool Contains(AnalysisStep step)
    {
        var top = step;
        while (top.Parent is { } parent)
        {
            top = parent;
        }

        return top == Root;
    }

    private static int CountBranch(AnalysisStep step) => 1 + step.Children.Sum(CountBranch);
}
