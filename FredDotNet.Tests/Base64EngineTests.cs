using NUnit.Framework;
using FredDotNet;

namespace FredDotNet.Tests;

[TestFixture]
public class Base64EngineTests
{
    [Test]
    public void Encode_EmptyString()
    {
        Assert.That(Base64Engine.Encode(""), Is.EqualTo(""));
    }

    [Test]
    public void Encode_HelloWorld()
    {
        Assert.That(Base64Engine.Encode("hello"), Is.EqualTo("aGVsbG8="));
    }

    [Test]
    public void Encode_ByteArray()
    {
        byte[] bytes = { 0x00, 0x01, 0x02, 0xFF };
        Assert.That(Base64Engine.Encode(bytes), Is.EqualTo("AAEC/w=="));
    }

    [Test]
    public void Decode_ValidBase64()
    {
        Assert.That(Base64Engine.Decode("aGVsbG8="), Is.EqualTo("hello"));
    }

    [Test]
    public void Decode_EmptyString()
    {
        Assert.That(Base64Engine.Decode(""), Is.EqualTo(""));
    }

    [Test]
    public void DecodeBytes_ValidBase64()
    {
        byte[] expected = { 0x00, 0x01, 0x02, 0xFF };
        Assert.That(Base64Engine.DecodeBytes("AAEC/w=="), Is.EqualTo(expected));
    }

    [Test]
    public void Decode_InvalidBase64_ThrowsBase64Exception()
    {
        Assert.Throws<Base64Exception>(() => Base64Engine.Decode("not!valid!base64!!!"));
    }

    [Test]
    public void DecodeBytes_InvalidBase64_ThrowsBase64Exception()
    {
        Assert.Throws<Base64Exception>(() => Base64Engine.DecodeBytes("not!valid!base64!!!"));
    }

    [Test]
    public void EncodeUrl_NoPaddingAndSafeChars()
    {
        // Standard: "AAEC/w==" -> URL-safe: "AAEC_w"
        string result = Base64Engine.EncodeUrl("\x00\x01\x02\xFF".Substring(0, 1)); // just test the replacement
        // Actually let's use a known value
        // "ab>?" standard = "YWI+Pw==" url-safe = "YWI-Pw"
        Assert.That(Base64Engine.EncodeUrl("ab>?"), Is.EqualTo("YWI-Pw"));
    }

    [Test]
    public void DecodeUrl_RestoresPaddingAndChars()
    {
        Assert.That(Base64Engine.DecodeUrl("YWI-Pw"), Is.EqualTo("ab>?"));
    }

    [Test]
    public void EncodeUrl_EmptyString()
    {
        Assert.That(Base64Engine.EncodeUrl(""), Is.EqualTo(""));
    }

    [Test]
    public void DecodeUrl_EmptyString()
    {
        Assert.That(Base64Engine.DecodeUrl(""), Is.EqualTo(""));
    }

    [Test]
    public void RoundTrip_Standard()
    {
        string original = "The quick brown fox jumps over the lazy dog. 123!@#$%";
        Assert.That(Base64Engine.Decode(Base64Engine.Encode(original)), Is.EqualTo(original));
    }

    [Test]
    public void RoundTrip_UrlSafe()
    {
        string original = "The quick brown fox jumps over the lazy dog. 123!@#$%";
        Assert.That(Base64Engine.DecodeUrl(Base64Engine.EncodeUrl(original)), Is.EqualTo(original));
    }

    [Test]
    public void Encode_Utf8MultiByte()
    {
        // "café" has a multi-byte char
        string encoded = Base64Engine.Encode("caf\u00e9");
        Assert.That(Base64Engine.Decode(encoded), Is.EqualTo("caf\u00e9"));
    }

    [Test]
    public void Base64Exception_HasInnerException()
    {
        try
        {
            Base64Engine.Decode("!!!invalid!!!");
            Assert.Fail("Expected Base64Exception");
        }
        catch (Base64Exception ex)
        {
            Assert.That(ex.InnerException, Is.Not.Null);
            Assert.That(ex.Message, Does.Contain("Invalid Base64"));
        }
    }
}
