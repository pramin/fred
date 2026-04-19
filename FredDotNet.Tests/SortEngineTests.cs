using NUnit.Framework;
using FredDotNet;

namespace FredDotNet.Tests;

[TestFixture]
public class SortEngineTests
{
    [Test]
    public void Sort_EmptyString()
    {
        Assert.That(SortEngine.Sort(""), Is.EqualTo(""));
    }

    [Test]
    public void Sort_SingleLine()
    {
        Assert.That(SortEngine.Sort("hello"), Is.EqualTo("hello"));
    }

    [Test]
    public void Sort_AlphabeticalDefault()
    {
        Assert.That(SortEngine.Sort("banana\napple\ncherry\n"),
            Is.EqualTo("apple\nbanana\ncherry\n"));
    }

    [Test]
    public void Sort_PreservesNoTrailingNewline()
    {
        Assert.That(SortEngine.Sort("b\na"), Is.EqualTo("a\nb"));
    }

    [Test]
    public void Sort_PreservesTrailingNewline()
    {
        Assert.That(SortEngine.Sort("b\na\n"), Is.EqualTo("a\nb\n"));
    }

    [Test]
    public void Sort_Reverse()
    {
        var opts = new SortOptions { Reverse = true };
        Assert.That(SortEngine.Sort("a\nb\nc\n", opts), Is.EqualTo("c\nb\na\n"));
    }

    [Test]
    public void Sort_Numeric()
    {
        var opts = new SortOptions { Numeric = true };
        Assert.That(SortEngine.Sort("10\n2\n1\n100\n", opts),
            Is.EqualTo("1\n2\n10\n100\n"));
    }

    [Test]
    public void Sort_NumericWithNonNumeric()
    {
        var opts = new SortOptions { Numeric = true };
        string result = SortEngine.Sort("10\nabc\n2\nxyz\n", opts);
        // Numbers come first, then non-numeric sorted alphabetically
        Assert.That(result, Is.EqualTo("2\n10\nabc\nxyz\n"));
    }

    [Test]
    public void Sort_IgnoreCase()
    {
        var opts = new SortOptions { IgnoreCase = true };
        string result = SortEngine.Sort("Banana\napple\nCherry\n", opts);
        Assert.That(result, Is.EqualTo("apple\nBanana\nCherry\n"));
    }

    [Test]
    public void Sort_Unique()
    {
        var opts = new SortOptions { Unique = true };
        Assert.That(SortEngine.Sort("b\na\nb\na\nc\n", opts),
            Is.EqualTo("a\nb\nc\n"));
    }

    [Test]
    public void Sort_UniqueCaseInsensitive()
    {
        var opts = new SortOptions { Unique = true, IgnoreCase = true };
        Assert.That(SortEngine.Sort("Apple\napple\nBanana\n", opts),
            Is.EqualTo("Apple\nBanana\n"));
    }

    [Test]
    public void Sort_KeyField_SecondField()
    {
        var opts = new SortOptions { KeyField = 2 };
        string input = "x banana\ny apple\nz cherry\n";
        string result = SortEngine.Sort(input, opts);
        Assert.That(result, Is.EqualTo("y apple\nx banana\nz cherry\n"));
    }

    [Test]
    public void Sort_KeyField_NumericSecondField()
    {
        var opts = new SortOptions { KeyField = 2, Numeric = true };
        string input = "a 10\nb 2\nc 100\n";
        string result = SortEngine.Sort(input, opts);
        Assert.That(result, Is.EqualTo("b 2\na 10\nc 100\n"));
    }

    [Test]
    public void Sort_KeyField_WithSeparator()
    {
        var opts = new SortOptions { KeyField = 2, FieldSeparator = "," };
        string input = "x,banana\ny,apple\nz,cherry\n";
        string result = SortEngine.Sort(input, opts);
        Assert.That(result, Is.EqualTo("y,apple\nx,banana\nz,cherry\n"));
    }

    [Test]
    public void Sort_KeyField_MissingField_TreatedAsEmpty()
    {
        var opts = new SortOptions { KeyField = 3 };
        string input = "a b c\nx y\n";
        string result = SortEngine.Sort(input, opts);
        // "x y" has no 3rd field (empty string), sorts before "c"
        Assert.That(result, Is.EqualTo("x y\na b c\n"));
    }

    [Test]
    public void Sort_Stable()
    {
        var opts = new SortOptions { KeyField = 1, Stable = true };
        string input = "a 1\na 3\na 2\n";
        string result = SortEngine.Sort(input, opts);
        // All same key, stable sort preserves original order
        Assert.That(result, Is.EqualTo("a 1\na 3\na 2\n"));
    }

    [Test]
    public void Sort_DefaultOptions_NullOptions()
    {
        // Passing null should work the same as default
        Assert.That(SortEngine.Sort("b\na\n", null), Is.EqualTo("a\nb\n"));
    }

    [Test]
    public void Sort_NumericReverse()
    {
        var opts = new SortOptions { Numeric = true, Reverse = true };
        Assert.That(SortEngine.Sort("1\n3\n2\n", opts), Is.EqualTo("3\n2\n1\n"));
    }

    [Test]
    public void Sort_CaseSensitiveDefault_UppercaseFirst()
    {
        // In ordinal comparison, uppercase letters have lower code points
        string result = SortEngine.Sort("b\nA\na\nB\n");
        Assert.That(result, Is.EqualTo("A\nB\na\nb\n"));
    }

    [Test]
    public void Sort_NumericFloats()
    {
        var opts = new SortOptions { Numeric = true };
        Assert.That(SortEngine.Sort("1.5\n0.5\n2.0\n", opts),
            Is.EqualTo("0.5\n1.5\n2.0\n"));
    }

    [Test]
    public void Sort_NumericNegative()
    {
        var opts = new SortOptions { Numeric = true };
        Assert.That(SortEngine.Sort("5\n-3\n0\n", opts),
            Is.EqualTo("-3\n0\n5\n"));
    }
}
