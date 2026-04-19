using NUnit.Framework;
using FredDotNet;

namespace FredDotNet.Tests;

[TestFixture]
public class WcEngineTests
{
    [Test]
    public void Count_EmptyString_ReturnsZeros()
    {
        var result = WcEngine.Count("");
        Assert.Multiple(() =>
        {
            Assert.That(result.Lines, Is.EqualTo(0));
            Assert.That(result.Words, Is.EqualTo(0));
            Assert.That(result.Characters, Is.EqualTo(0));
            Assert.That(result.Bytes, Is.EqualTo(0));
        });
    }

    [Test]
    public void Count_SingleWord_NoNewline()
    {
        var result = WcEngine.Count("hello");
        Assert.Multiple(() =>
        {
            Assert.That(result.Lines, Is.EqualTo(0));
            Assert.That(result.Words, Is.EqualTo(1));
            Assert.That(result.Characters, Is.EqualTo(5));
            Assert.That(result.Bytes, Is.EqualTo(5));
        });
    }

    [Test]
    public void Count_MultipleLines()
    {
        var result = WcEngine.Count("hello world\nfoo bar\n");
        Assert.Multiple(() =>
        {
            Assert.That(result.Lines, Is.EqualTo(2));
            Assert.That(result.Words, Is.EqualTo(4));
            Assert.That(result.Characters, Is.EqualTo(20));
            Assert.That(result.Bytes, Is.EqualTo(20));
        });
    }

    [Test]
    public void Count_Utf8MultiByte_BytesExceedCharacters()
    {
        // Each emoji is 1 char (surrogate pair = 2 chars in UTF-16) but 4 bytes in UTF-8
        // Actually, \u00e9 is 1 char UTF-16, 2 bytes UTF-8
        var result = WcEngine.Count("caf\u00e9");
        Assert.Multiple(() =>
        {
            Assert.That(result.Words, Is.EqualTo(1));
            Assert.That(result.Characters, Is.EqualTo(4));
            Assert.That(result.Bytes, Is.EqualTo(5)); // c=1, a=1, f=1, \u00e9=2
        });
    }

    [Test]
    public void Count_LeadingAndTrailingWhitespace()
    {
        var result = WcEngine.Count("  hello  world  ");
        Assert.Multiple(() =>
        {
            Assert.That(result.Words, Is.EqualTo(2));
            Assert.That(result.Characters, Is.EqualTo(16));
        });
    }

    [Test]
    public void Count_TabsAsWhitespace()
    {
        var result = WcEngine.Count("a\tb\tc");
        Assert.That(result.Words, Is.EqualTo(3));
    }

    [Test]
    public void Count_OnlyWhitespace()
    {
        var result = WcEngine.Count("   \n  \n");
        Assert.Multiple(() =>
        {
            Assert.That(result.Lines, Is.EqualTo(2));
            Assert.That(result.Words, Is.EqualTo(0));
        });
    }

    [Test]
    public void Count_TextReader_MatchesStringOverload()
    {
        string input = "hello world\nfoo bar baz\n";
        var fromString = WcEngine.Count(input);
        var fromReader = WcEngine.Count(new StringReader(input));

        Assert.Multiple(() =>
        {
            Assert.That(fromReader.Lines, Is.EqualTo(fromString.Lines));
            Assert.That(fromReader.Words, Is.EqualTo(fromString.Words));
            Assert.That(fromReader.Characters, Is.EqualTo(fromString.Characters));
            Assert.That(fromReader.Bytes, Is.EqualTo(fromString.Bytes));
        });
    }

    [Test]
    public void Count_TextReader_Empty()
    {
        var result = WcEngine.Count(new StringReader(""));
        Assert.Multiple(() =>
        {
            Assert.That(result.Lines, Is.EqualTo(0));
            Assert.That(result.Words, Is.EqualTo(0));
            Assert.That(result.Characters, Is.EqualTo(0));
            Assert.That(result.Bytes, Is.EqualTo(0));
        });
    }

    [Test]
    public void Count_SingleNewline()
    {
        var result = WcEngine.Count("\n");
        Assert.Multiple(() =>
        {
            Assert.That(result.Lines, Is.EqualTo(1));
            Assert.That(result.Words, Is.EqualTo(0));
            Assert.That(result.Characters, Is.EqualTo(1));
        });
    }
}
