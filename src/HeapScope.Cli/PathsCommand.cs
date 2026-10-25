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

using System.CommandLine;
using System.Globalization;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace HeapScope.Cli;

/// <summary>
/// The <c>paths</c> command (sprint 2.2): why an object is alive (its shortest paths from up to N roots, merged into one
/// tree from the object toward its roots) or why the objects of a type are alive (their key retention paths). Runs of
/// framework steps are folded unless <c>--expand</c>.
/// </summary>
internal static partial class CliCommands
{
    private static Command BuildPathsCommand(Func<IServiceProvider> services)
    {
        var dump = new Argument<FileInfo>("snapshot") { Description = "Path of the full memory dump (.dmp) or gcdump (.gcdump)." };
        var address = new Option<string?>("--address") { Description = "An object's address in hex (0x1f2a3b40): why is it alive?" };
        var type = new Option<string?>("--type") { Description = "A type's full name: why are its objects alive (their key retention paths)?" };
        var module = new Option<string?>("--module") { Description = "With --type: the type's module file name, when several modules have the name." };
        var max = new Option<int?>("--max") { Description = "Roots to find for --address (1 to 50, default 10), key paths to list for --type (1 to 100, default 20)." };
        var expand = new Option<bool>("--expand") { Description = "Show every framework step instead of folding runs of them." };
        var format = new Option<string>("--format") { Description = "Output: text or json.", DefaultValueFactory = _ => "text" };
        format.AcceptOnlyFromAmong("text", "json");

        var command = new Command("paths", "Explain why an object (--address) or the objects of a type (--type) are alive: shortest paths from GC roots, roots in plain language, field names on full dumps. Exit codes: 0 ok, 1 error (no such object or type), 64 usage, 130 canceled.");
        command.Arguments.Add(dump);
        command.Options.Add(address);
        command.Options.Add(type);
        command.Options.Add(module);
        command.Options.Add(max);
        command.Options.Add(expand);
        command.Options.Add(format);
        command.Validators.Add(result =>
        {
            var byAddress = result.GetResult(address) is not null;
            if (byAddress == (result.GetResult(type) is not null))
            {
                result.AddError("Give either --address or --type.");
            }
            else if (byAddress && !TryParseAddress(result.GetValue(address), out _))
            {
                result.AddError($"'{result.GetValue(address)}' is not an address: use hex, such as 0x1f2a3b40.");
            }
            else if (byAddress && result.GetResult(module) is not null)
            {
                result.AddError("--module goes with --type.");
            }
            else if (result.GetResult(max) is { Implicit: false } && (result.GetValue(max) is < 1 or > 100 || (byAddress && result.GetValue(max) > 50)))
            {
                result.AddError("--max must be 1 to 50 with --address, 1 to 100 with --type.");
            }
        });
        command.SetAction((parseResult, ct) => RunAsync(parseResult, async (output, error) =>
        {
            var handle = await OpenForQueryAsync(parseResult.GetValue(dump)!, error, ct).ConfigureAwait(false);
            if (handle is null)
            {
                return Failure;
            }

            var session = services().GetRequiredService<SnapshotSession>();
            await WriteNotesAsync(error, "Snapshot", handle).ConfigureAwait(false);
            var json = parseResult.GetValue(format) == "json";
            var fold = !parseResult.GetValue(expand);
            if (parseResult.GetValue(address) is { } text)
            {
                TryParseAddress(text, out var at);
                var paths = await session.QueryObjectPathsAsync(handle, at, parseResult.GetValue(max) ?? 10, progress: null, ct).ConfigureAwait(false);
                await WritePathNotesAsync(error, paths.CompleteGraph, paths.StoppedEarly, paths.FieldsNote).ConfigureAwait(false);
                if (json)
                {
                    await WriteJsonAsync(output, paths).ConfigureAwait(false);
                }
                else
                {
                    await WriteObjectPathsAsync(output, paths, fold).ConfigureAwait(false);
                }

                return Success;
            }

            var typeName = parseResult.GetValue(type)!;
            var moduleName = parseResult.GetValue(module) ?? await FindModuleAsync(session, handle, typeName, error, ct).ConfigureAwait(false);
            if (moduleName is null)
            {
                return Failure;
            }

            var result = await session.QueryTypePathsAsync(handle, typeName, moduleName, parseResult.GetValue(max) ?? 20, progress: null, ct).ConfigureAwait(false);
            if (result.MatchedTypes == 0)
            {
                await error.WriteLineAsync($"Error: there is no type {typeName} in module '{moduleName}' in this snapshot.").ConfigureAwait(false);
                return Failure;
            }

            await WritePathNotesAsync(error, result.CompleteGraph, stoppedEarly: false, fieldsNote: null).ConfigureAwait(false);
            if (json)
            {
                await WriteJsonAsync(output, result).ConfigureAwait(false);
                return Success;
            }

            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Key retention paths of {typeName} ({moduleName}): {result.Objects:N0} objects, {result.Unreachable:N0} unreachable")).ConfigureAwait(false);
            await output.WriteLineAsync($"{"Objects",12} {"Bytes",14}  Root, then the path from it").ConfigureAwait(false);
            foreach (var path in result.Paths)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{path.Objects,12:N0} {path.Bytes,14:N0}  {path.Root.Description}")).ConfigureAwait(false);
                await output.WriteLineAsync(new string(' ', 30) + PathText(path, fold)).ConfigureAwait(false);
            }

            if (result.OtherPaths > 0)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{result.OtherObjects,12:N0} {result.OtherBytes,14:N0}  {result.OtherPaths:N0} other paths")).ConfigureAwait(false);
            }

            if (result.Unreachable > 0)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{result.Unreachable,12:N0} {result.UnreachableBytes,14:N0}  {result.UnreachableNote}")).ConfigureAwait(false);
            }

            return Success;
        }));
        return command;
    }

    private static async Task WritePathNotesAsync(TextWriter error, bool completeGraph, bool stoppedEarly, string? fieldsNote)
    {
        if (!completeGraph)
        {
            await error.WriteLineAsync("Note: this snapshot is incomplete (sampled or truncated), so some paths may be missing.").ConfigureAwait(false);
        }

        if (stoppedEarly)
        {
            await error.WriteLineAsync("Note: the search stopped at its limit, or the tree at its size limit, so more roots may hold the object.").ConfigureAwait(false);
        }

        if (fieldsNote is not null)
        {
            await error.WriteLineAsync("Note: " + fieldsNote).ConfigureAwait(false);
        }
    }

    private static async Task WriteObjectPathsAsync(TextWriter output, ObjectPathsResult paths, bool fold)
    {
        var target = paths.Nodes[0];
        if (!paths.Reachable)
        {
            await output.WriteLineAsync($"{Hex(target.Address)} ({target.TypeName}): {paths.UnreachableNote}").ConfigureAwait(false);
            return;
        }

        await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
            $"Retention paths of {Hex(target.Address)} ({target.TypeName}): {paths.RootNodeIds.Count:N0} roots found")).ConfigureAwait(false);
        await output.WriteLineAsync("Key paths:").ConfigureAwait(false);
        foreach (var path in paths.KeyPaths)
        {
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"  {path.Objects,4:N0} {(path.Objects == 1 ? "root " : "roots")}  {path.Root.Description}")).ConfigureAwait(false);
            await output.WriteLineAsync("              " + PathText(path, fold)).ConfigureAwait(false);
        }

        await output.WriteLineAsync("Paths, from the object toward its roots:").ConfigureAwait(false);
        await output.WriteLineAsync($"{Hex(target.Address)}  {target.TypeName}").ConfigureAwait(false);
        var holders = paths.Nodes.Skip(1).ToLookup(n => n.ChildId);
        await WriteHoldersAsync(output, holders, 0, 1, fold).ConfigureAwait(false);
    }

    /// <summary>
    /// Prints the tree below <paramref name="child"/> depth first (review H3): each holder, then its own holders, before the
    /// next holder of the same node, closest root first. Folds runs of framework steps; the worker already folded runs of
    /// one type and cut long paths (review H1), so the depth stays small.
    /// </summary>
    internal static async Task WriteHoldersAsync(TextWriter output, ILookup<int, RetentionNodeDto> holders, int child, int depth, bool fold)
    {
        var pending = new Stack<(RetentionNodeDto Node, int Level)>();
        foreach (var node in holders[child].Reverse())
        {
            pending.Push((node, depth));
        }

        while (pending.Count > 0)
        {
            var (node, level) = pending.Pop();
            var shown = node;
            var folded = 0;
            while (fold && shown.IsFramework && holders[shown.Id].Count() == 1)
            {
                folded++;
                shown = holders[shown.Id].Single();
            }

            var indent = new string(' ', level * 2);
            if (folded > 0)
            {
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture, $"{indent}<- ({folded:N0} framework objects)")).ConfigureAwait(false);
            }

            await output.WriteLineAsync(indent + "<- " + NodeText(shown)).ConfigureAwait(false);
            foreach (var holder in holders[shown.Id].Reverse())
            {
                pending.Push((holder, level + 1));
            }
        }
    }

    /// <summary>One node: how it holds the next one, its address and type, the objects folded into it, its root.</summary>
    internal static string NodeText(RetentionNodeDto node)
    {
        if (node.Address == 0)
        {
            return node.TypeName;
        }

        var how = node.Field ?? (node.ViaDependentHandle ? "(dependent handle)" : null);
        var folded = node.Folded > 0 ? string.Create(CultureInfo.InvariantCulture, $" (and {node.Folded:N0} more of this type)") : string.Empty;
        var root = node.Root is { } r ? "   ROOT: " + r.Description : string.Empty;
        return $"{(how is null ? string.Empty : how + " of ")}{Hex(node.Address)}  {node.TypeName}{folded}{root}";
    }

    /// <summary>A key path as text, root first: "A > B > (2 framework types) > C > target".</summary>
    private static string PathText(KeyPathDto path, bool fold)
    {
        var parts = new List<string>();
        var run = 0;
        foreach (var step in path.Steps)
        {
            if (fold && step.IsFramework)
            {
                run++;
                continue;
            }

            if (run > 0)
            {
                parts.Add(run == 1 ? "(1 framework type)" : $"({run} framework types)");
                run = 0;
            }

            parts.Add(step.TypeName);
        }

        if (run > 0)
        {
            parts.Add(run == 1 ? "(1 framework type)" : $"({run} framework types)");
        }

        parts.Add(path.TargetType);
        return string.Join(" > ", parts);
    }
}
