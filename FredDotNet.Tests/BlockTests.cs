using NUnit.Framework;
using FredDotNet;
using System.Collections.ObjectModel;

namespace FredDotNet.Tests;

/// <summary>
/// WI-009: Block support tests ({ } grouping)
/// </summary>
[TestFixture]
public class BlockTests
{
    // --- Test 1: Simple block with line number address ---

    [Test]
    public void Block_LineAddressed_AppliesOnlyToMatchingLine()
    {
        // "2 { s/a/b/ }" — substitution only on line 2 (first occurrence only, no g flag)
        var script = SedParser.Parse("2{s/a/b/}");
        var result = script.Transform("aaa\naaa\naaa");
        Assert.That(result, Is.EqualTo("aaa\nbaa\naaa"));
    }

    // --- Test 2: Block without address applies to every line ---

    [Test]
    public void Block_NoAddress_AppliesOnEveryLine()
    {
        var script = SedParser.Parse("{s/a/b/}");
        var result = script.Transform("aaa\naaa");
        Assert.That(result, Is.EqualTo("baa\nbaa"));
    }

    // --- Test 3: Nested blocks ---

    [Test]
    public void Block_Nested_InnerBlockRequiresBothConditions()
    {
        // Outer block: line 2.  Inner block: /foo/.
        // Line 1 "foo":  outer doesn't match (not line 2) — no change.
        // Line 2 "foo":  outer matches, inner matches — substitution.
        // Line 3 "foo":  outer doesn't match — no change.
        var script = SedParser.Parse("2{/foo/{s/foo/bar/}}");
        var result = script.Transform("foo\nfoo\nfoo");
        Assert.That(result, Is.EqualTo("foo\nbar\nfoo"));
    }

    // --- Test 4: Block with negation ---

    [Test]
    public void Block_Negated_AppliesOnAllExceptMatchingLine()
    {
        // "2!{ s/a/b/ }" — substitution on every line EXCEPT line 2 (first occurrence only, no g flag)
        var script = SedParser.Parse("2!{s/a/b/}");
        var result = script.Transform("aaa\naaa\naaa");
        Assert.That(result, Is.EqualTo("baa\naaa\nbaa"));
    }

    // --- Test 5: Empty block is a no-op ---

    [Test]
    public void Block_Empty_IsNoOp()
    {
        var script = SedParser.Parse("2{}");
        var result = script.Transform("hello\nworld");
        Assert.That(result, Is.EqualTo("hello\nworld"));
    }

    // --- Test 6: Multiple commands in a block ---

    [Test]
    public void Block_MultipleCommands_AllApplyOnMatch()
    {
        // Line 2 gets both substitutions
        var script = SedParser.Parse("2{s/a/b/;s/c/d/}");
        var result = script.Transform("ac\nac\nac");
        Assert.That(result, Is.EqualTo("ac\nbd\nac"));
    }

    // --- Test 7: Multiple blocks at the same level ---

    [Test]
    public void Block_MultipleBlocksAtTopLevel_EachAppliesCorrectly()
    {
        // s/a/b/ without g — only first occurrence; "aaa" → "baa", "ccc" → "dcc"
        var script = SedParser.Parse("1{s/a/b/}\n2{s/c/d/}");
        var result = script.Transform("aaa\nccc");
        Assert.That(result, Is.EqualTo("baa\ndcc"));
    }

    // --- Test 8: Block containing a delete command ---

    [Test]
    public void Block_WithDelete_DeletesMatchingLine()
    {
        var script = SedParser.Parse("2{d}");
        var result = script.Transform("keep\ndelete\nkeep");
        Assert.That(result, Is.EqualTo("keep\nkeep"));
    }

    // --- Test 9: Block whose body is separated by newlines ---

    [Test]
    public void Block_NewlineSeparatedBody_Works()
    {
        // s/a/b/ without g — only first occurrence
        var script = SedParser.Parse("2{\ns/a/b/\n}");
        var result = script.Transform("aaa\naaa\naaa");
        Assert.That(result, Is.EqualTo("aaa\nbaa\naaa"));
    }

    // --- Test 10: Classic -n '/foo/{ s/foo/bar/; p }' pattern ---

    [Test]
    public void Block_PatternAddress_WithSuppressAndPrint_OnlyOutputsMatchedLines()
    {
        // -n combined with /foo/{ s/foo/bar/; p } — only lines originally containing "foo"
        // are printed, after substitution.
        var script = SedParser.Parse("/foo/{s/foo/bar/;p}", suppressDefaultOutput: true);
        var result = script.Transform("foo\nbaz\nfoo");
        Assert.That(result, Is.EqualTo("bar\nbar"));
    }

    // --- Test 11: Block with flow control ---

    [Test]
    public void Block_FlowControl_BranchSkipsRestOfBlock()
    {
        // { s/a/b/; b end; s/b/c/; :end }
        // The branch skips s/b/c/, so 'a' turns to 'b' (first occurrence only) but never to 'c'
        var script = SedParser.Parse("{s/a/b/;bend;s/b/c/;:end}");
        var result = script.Transform("aaa");
        Assert.That(result, Is.EqualTo("baa"));
    }

    // --- Test 12: Range address on block ---

    [Test]
    public void Block_RangeAddress_AppliesOnAllLinesInRange()
    {
        // s/a/b/ without g — only first occurrence per line
        var script = SedParser.Parse("1,3{s/a/b/}");
        var result = script.Transform("aaa\naaa\naaa\naaa");
        Assert.That(result, Is.EqualTo("baa\nbaa\nbaa\naaa"));
    }

    // --- Test 13: Block parser exposes CommandType.Block on top-level command ---

    [Test]
    public void Block_ParsedCommand_HasBlockType()
    {
        var script = SedParser.Parse("2{s/a/b/}");
        // The top-level command list should contain exactly one Block command.
        Assert.That(script.Commands, Has.Count.EqualTo(1));
        Assert.That(script.Commands[0].Type, Is.EqualTo(CommandType.Block));
        Assert.That(script.Commands[0].Block, Is.Not.Null);
        Assert.That(script.Commands[0].Block, Has.Count.EqualTo(1));
    }

    // --- Test 14: BlockCommand factory ---

    [Test]
    public void BlockCommand_Factory_CreatesCommandWithBlockType()
    {
        var inner = SedCommand.Substitute(SedAddress.All(), "a", "b");
        var block = SedCommand.BlockCommand(
            SedAddress.LineNumber(2), negated: false,
            block: new ReadOnlyCollection<SedCommand>(new[] { inner }));

        Assert.That(block.Type, Is.EqualTo(CommandType.Block));
        Assert.That(block.Block, Has.Count.EqualTo(1));
        Assert.That(block.Address.Type, Is.EqualTo(AddressType.LineNumber));
    }

    // -----------------------------------------------------------------------
    // M3: Additional coverage — n, a/i, q, h/g/x, cross-block t/T, N/D
    // -----------------------------------------------------------------------

    // --- Test 15: n inside a block advances input ---

    [Test]
    public void Block_Next_PrintsCurrentAndAdvancesToNextLine()
    {
        // 2{ n; s/a/b/ }
        // Line 2: block fires, n prints line 2 and loads line 3 into pattern space.
        // Then s/a/b/ transforms "aaa" (line 3) to "baa".
        // Line 3 is not output again at end of cycle (consumed by n inside block).
        // Output: line1="aaa", line2="aaa" (printed by n), line3="baa"
        var script = SedParser.Parse("2{n;s/a/b/}");
        var result = script.Transform("aaa\naaa\naaa");
        Assert.That(result, Is.EqualTo("aaa\naaa\nbaa"));
    }

    // --- Test 16: i (insert) inside a block emits text before pattern space ---

    [Test]
    public void Block_Insert_EmitsTextBeforePatternSpace()
    {
        // 2{ i\BEFORE }
        var script = SedParser.Parse("2{i\\BEFORE}");
        var result = script.Transform("one\ntwo\nthree");
        Assert.That(result, Is.EqualTo("one\nBEFORE\ntwo\nthree"));
    }

    // --- Test 17: a (append) inside a block emits text after pattern space ---

    [Test]
    public void Block_Append_EmitsTextAfterPatternSpace()
    {
        // 2{ a\AFTER }
        var script = SedParser.Parse("2{a\\AFTER}");
        var result = script.Transform("one\ntwo\nthree");
        Assert.That(result, Is.EqualTo("one\ntwo\nAFTER\nthree"));
    }

    // --- Test 18: q inside a block quits after outputting current line ---

    [Test]
    public void Block_Quit_StopsAfterCurrentLine()
    {
        // 2{ q } — quit after printing line 2
        var script = SedParser.Parse("2{q}");
        var result = script.Transform("one\ntwo\nthree");
        Assert.That(result, Is.EqualTo("one\ntwo\n"));
    }

    // --- Test 19: h/g (hold/get) inside blocks ---

    [Test]
    public void Block_HoldAndGet_ExchangesPatternAndHoldSpace()
    {
        // 1{ h }  2{ g } — line 2 gets replaced by a copy of line 1
        var script = SedParser.Parse("1{h}\n2{g}");
        var result = script.Transform("saved\noriginal\nuntouched");
        Assert.That(result, Is.EqualTo("saved\nsaved\nuntouched"));
    }

    // --- Test 20: x (exchange) inside a block ---

    [Test]
    public void Block_Exchange_SwapsPatternAndHoldSpace()
    {
        // 1{ x }  2{ x } — line 1 is saved to hold; on line 2 they swap back.
        // After 1{x}: hold="line1", pattern="" → output ""
        // After 2{x}: hold="line2", pattern="line1" → output "line1"
        // Line 3 is unaffected.
        var script = SedParser.Parse("1{x}\n2{x}");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("\nline1\nline3"));
    }

    // --- Test 21: t (branch-if-substituted) inside a block ---

    [Test]
    public void Block_TestBranch_SkipsCommandWhenSubstitutionMade()
    {
        // { s/foo/bar/; t end; s/bar/baz/; :end }
        // If s/foo/bar/ fires, t branches to :end and s/bar/baz/ is skipped.
        // Input "foo" → "bar" (not "baz").  Input "other" → s/foo/bar/ doesn't fire,
        // t doesn't branch, s/bar/baz/ doesn't match "other" either → "other".
        var script = SedParser.Parse("{s/foo/bar/;tend;s/bar/baz/;:end}");
        var result = script.Transform("foo\nother\nbar");
        Assert.That(result, Is.EqualTo("bar\nother\nbaz"));
    }

    // --- Test 22: T (branch-if-NOT-substituted) inside a block ---

    [Test]
    public void Block_TestNotBranch_SkipsCommandWhenNoSubstitutionMade()
    {
        // { s/foo/bar/; T end; s/bar/baz/; :end }
        // T branches when NO substitution has been made in this cycle.
        // Input "foo": s/foo/bar/ fires, T does NOT branch, s/bar/baz/ fires → "baz".
        // Input "other": s/foo/bar/ doesn't fire, T branches to :end → "other".
        var script = SedParser.Parse("{s/foo/bar/;Tend;s/bar/baz/;:end}");
        var result = script.Transform("foo\nother");
        Assert.That(result, Is.EqualTo("baz\nother"));
    }

    // --- Test 23: Cross-block branch propagates to outer label ---

    [Test]
    public void Block_CrossBlockBranch_PropagatesUnresolvedLabelToOuterScope()
    {
        // 2{ b outer }  :outer  s/x/y/
        // On line 2 the block branches to :outer at the top level; s/x/y/ fires.
        // On line 1 the block does not fire; s/x/y/ still fires.
        var script = SedParser.Parse("2{bouter}\n:outer\ns/x/y/");
        var result = script.Transform("xxx\nxxx\nxxx");
        Assert.That(result, Is.EqualTo("yxx\nyxx\nyxx"));
    }

    // --- Test 24: N (next-append) inside a block ---

    [Test]
    public void Block_NextAppend_AppendsNextLineToPatternSpace()
    {
        // 1{ N; s/\n/ / }  — join line 1 and line 2 with a space
        var script = SedParser.Parse("1{N;s/\\n/ /}");
        var result = script.Transform("hello\nworld\nthird");
        Assert.That(result, Is.EqualTo("hello world\nthird"));
    }

    // --- Test 25: D (delete first line) inside a block ---

    [Test]
    public void Block_DeleteFirstLine_RemovesUpToFirstEmbeddedNewline()
    {
        // 1{ N; D }
        // After N, pattern space = "line1\nline2".
        // D removes "line1\n", leaves "line2", restarts cycle.
        // "line2" passes through normally; "line3" passes through normally.
        var script = SedParser.Parse("1{N;D}");
        var result = script.Transform("line1\nline2\nline3");
        Assert.That(result, Is.EqualTo("line2\nline3"));
    }

    // --- Test 26: n inside a block updates lineNumber for subsequent address matching (B1 regression) ---

    [Test]
    public void Block_Next_LineNumberUpdatedAfterAdvance()
    {
        // 2{ n }  3{ s/x/y/g }
        // Line 2: block fires; n prints line 2 and loads line 3 into pattern space.
        // lineNumber must be updated to 3 so the 3{...} address matches and s/x/y/g fires.
        // Without the B1 fix, lineNumber stays 2 and the 3{} block never fires on the
        // consumed line, leaving "xxx" unchanged.
        // Output: line1 unchanged, line2 printed by n, line3 transformed to "yyy".
        var script = SedParser.Parse("2{n}\n3{s/x/y/g}");
        var result = script.Transform("aaa\naaa\nxxx");
        Assert.That(result, Is.EqualTo("aaa\naaa\nyyy"));
    }
}
