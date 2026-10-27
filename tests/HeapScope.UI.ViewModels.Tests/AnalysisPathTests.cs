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
using HeapScope.UI.ViewModels;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>The branching analysis path (sprint 2.7).</summary>
public sealed class AnalysisPathTests
{
    [Fact]
    public void Back_goes_to_the_parent_and_forward_to_the_child_shown_last()
    {
        var path = new AnalysisPath(new Subject("Types"));
        var a = path.Open(new Subject("A"));
        var b = path.Open(new Subject("B"));
        path.Back();
        path.Back();
        var c = path.Open(new Subject("C"));
        path.Back();

        path.Forward().ShouldBe(c, "the branch shown last");
        path.Select(b);
        path.Back().ShouldBe(a);
        path.Back();
        path.Forward().ShouldBe(a, "selecting B made its branch the way down");
        path.Steps.Select(s => s.Title).ShouldBe(["Types", "A", "B", "C"]);
        path.Steps.Select(s => s.Depth).ShouldBe([0, 1, 2, 1]);
    }

    [Fact]
    public void Closing_a_step_closes_its_subjects_and_moves_off_a_closed_trail()
    {
        var path = new AnalysisPath(new Subject("Types"));
        var a = new Subject("A");
        var b = new Subject("B");
        var stepA = path.Open(a);
        path.Open(b);

        path.Close(stepA).ShouldBe(2);

        (a.Closed, b.Closed).ShouldBe((true, true));
        path.Current.ShouldBe(path.Root);
        path.Count.ShouldBe(1);
        path.Close(path.Root).ShouldBe(0);
        Should.Throw<ArgumentException>(() => path.Select(stepA));
    }

    [Fact]
    public void Past_the_cap_the_least_recently_shown_leaf_off_the_trail_is_closed()
    {
        var path = new AnalysisPath(new Subject("Types"));
        var subjects = new List<Subject>();
        for (var i = 0; i < AnalysisPath.MaxSteps - 1; i++)
        {
            var subject = new Subject("S" + i);
            subjects.Add(subject);
            path.Open(subject);
            path.Back();
        }

        path.Count.ShouldBe(AnalysisPath.MaxSteps);
        path.Open(new Subject("New"));

        path.Count.ShouldBe(AnalysisPath.MaxSteps);
        subjects[0].Closed.ShouldBeTrue("shown longest ago");
        subjects.Skip(1).ShouldAllBe(s => !s.Closed);
    }

    [Fact]
    public void A_trail_longer_than_the_cap_loses_its_oldest_step_below_the_root()
    {
        var path = new AnalysisPath(new Subject("Types"));
        var subjects = Enumerable.Range(0, AnalysisPath.MaxSteps + 2).Select(i => new Subject("S" + i)).ToList();
        foreach (var subject in subjects)
        {
            path.Open(subject);
        }

        path.Count.ShouldBe(AnalysisPath.MaxSteps);
        path.Trail.Count.ShouldBe(AnalysisPath.MaxSteps);
        subjects.Take(3).ShouldAllBe(s => s.Closed);
        path.Trail[1].Title.ShouldBe("S3");
        path.Current.Title.ShouldBe("S" + (AnalysisPath.MaxSteps + 1));
    }

    [Fact]
    public void A_retainers_step_shows_the_subject_it_came_from_on_the_snapshot_tab()
    {
        var path = new AnalysisPath(new Subject("Types"));
        var a = new Subject("A");
        path.Open(a);

        var step = path.Open(NavigationTarget.Retainers(null!, address: 0x10));

        step.Title.ShouldBe("Retainers of 0x10");
        step.SubjectShown.ShouldBe(a);
        path.Open(NavigationTarget.RetainedBySet(null!, "App.Order", null)).Title.ShouldBe("Retained by the set of App.Order");
    }

    [Fact]
    public void A_spliced_retainers_step_hands_its_children_to_the_root_and_ids_stay()
    {
        var path = new AnalysisPath(new Subject("Types"));
        var retainers = path.Open(NavigationTarget.Retainers(null!, address: 0x10));
        for (var i = 0; i < AnalysisPath.MaxSteps; i++)
        {
            path.Open(new Subject("S" + i));
        }

        path.Steps.ShouldNotContain(retainers);
        path.Root.Children.ShouldHaveSingleItem().Title.ShouldBe("S1", "S0 was spliced next; the root keeps the trail");
        path.Steps.Select(s => s.Id).ShouldBeUnique();
        path.Count.ShouldBe(AnalysisPath.MaxSteps);
    }

    [Fact]
    public void Closing_the_branch_forward_would_take_moves_forward_to_another_and_close_all_keeps_the_root()
    {
        var path = new AnalysisPath(new Subject("Types"));
        var a = path.Open(new Subject("A"));
        path.Back();
        var b = path.Open(new Subject("B"));
        path.Back();

        path.Close(b);
        path.Forward().ShouldBe(a);

        var subject = new Subject("C");
        path.Open(subject);
        path.CloseAll();
        subject.Closed.ShouldBeTrue();
        (path.Count, path.Current).ShouldBe((1, path.Root));
        path.CanGoForward.ShouldBeFalse();
    }

    private sealed class Subject(string title) : INavigationSubject
    {
        public string Title { get; } = title;

        public SnapshotHandle? Snapshot => null;

        public bool Closed { get; private set; }

        public void Close() => Closed = true;
    }
}
