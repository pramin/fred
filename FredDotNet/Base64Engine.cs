using System.Text;

namespace FredDotNet;

/// <summary>
/// Exception thrown when Base64 encoding or decoding fails.
/// </summary>
public sealed class Base64Exception : Exception
{
    /// <summary>Creates a <see cref="Base64Exception"/> with the specified error message.</summary>
    public Base64Exception(string message) : base(message) { }

    /// <summary>Creates a <see cref="Base64Exception"/> with the specified error message and inner exception.</summary>
    public Base64Exception(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Encodes and decodes Base64 and URL-safe Base64 strings.
/// Wraps <see cref="Convert.ToBase64String(byte[])"/> and <see cref="Convert.FromBase64String(string)"/>
/// with proper error handling and URL-safe variant support.
/// </summary>
public static class Base64Engine
{
    /// <summary>Encode a string to standard Base64 using UTF-8.</summary>
    /// <param name="input">The string to encode.</param>
    /// <returns>The Base64-encoded representation.</returns>
    public static string Encode(string input)
    {
        byte[] inputBytes = Encoding.UTF8.GetBytes(input);
        return Convert.ToBase64String(inputBytes);
    }

    /// <summary>Encode a byte array to standard Base64.</summary>
    /// <param name="input">The bytes to encode.</param>
    /// <returns>The Base64-encoded representation.</returns>
    public static string Encode(byte[] input)
    {
        return Convert.ToBase64String(input);
    }

    /// <summary>Decode a standard Base64 string to a UTF-8 string.</summary>
    /// <param name="base64Input">The Base64-encoded string.</param>
    /// <returns>The decoded UTF-8 string.</returns>
    /// <exception cref="Base64Exception">Thrown when the input is not valid Base64.</exception>
    public static string Decode(string base64Input)
    {
        byte[] bytes = DecodeBytesInternal(base64Input);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Decode a standard Base64 string to a byte array.</summary>
    /// <param name="base64Input">The Base64-encoded string.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="Base64Exception">Thrown when the input is not valid Base64.</exception>
    public static byte[] DecodeBytes(string base64Input)
    {
        return DecodeBytesInternal(base64Input);
    }

    /// <summary>Encode a string to URL-safe Base64 (no padding, <c>+/</c> replaced with <c>-_</c>).</summary>
    /// <param name="input">The string to encode.</param>
    /// <returns>The URL-safe Base64-encoded representation.</returns>
    public static string EncodeUrl(string input)
    {
        string standard = Encode(input);
        return ToUrlSafe(standard);
    }

    /// <summary>Decode a URL-safe Base64 string to a UTF-8 string.</summary>
    /// <param name="base64UrlInput">The URL-safe Base64-encoded string.</param>
    /// <returns>The decoded UTF-8 string.</returns>
    /// <exception cref="Base64Exception">Thrown when the input is not valid URL-safe Base64.</exception>
    public static string DecodeUrl(string base64UrlInput)
    {
        string standard = FromUrlSafe(base64UrlInput);
        return Decode(standard);
    }

    private static byte[] DecodeBytesInternal(string base64Input)
    {
        try
        {
            return Convert.FromBase64String(base64Input);
        }
        catch (FormatException ex)
        {
            throw new Base64Exception($"Invalid Base64 input: {ex.Message}", ex);
        }
    }

    private static string ToUrlSafe(string standard)
    {
        // Replace +/ with -_ and strip trailing padding
        int len = standard.Length;
        while (len > 0 && standard[len - 1] == '=')
            len--;

        // Use stackalloc for small strings, otherwise rent
        if (len <= 256)
        {
            Span<char> buf = stackalloc char[len];
            for (int i = 0; i < len; i++)
            {
                char c = standard[i];
                buf[i] = c switch
                {
                    '+' => '-',
                    '/' => '_',
                    _ => c,
                };
            }
            return new string(buf);
        }
        else
        {
            char[] buf = new char[len];
            for (int i = 0; i < len; i++)
            {
                char c = standard[i];
                buf[i] = c switch
                {
                    '+' => '-',
                    '/' => '_',
                    _ => c,
                };
            }
            return new string(buf);
        }
    }

    private static string FromUrlSafe(string urlSafe)
    {
        // Restore +/ from -_ and add padding
        int paddingNeeded = (4 - (urlSafe.Length % 4)) % 4;
        int totalLen = urlSafe.Length + paddingNeeded;

        if (totalLen <= 256)
        {
            Span<char> buf = stackalloc char[totalLen];
            for (int i = 0; i < urlSafe.Length; i++)
            {
                char c = urlSafe[i];
                buf[i] = c switch
                {
                    '-' => '+',
                    '_' => '/',
                    _ => c,
                };
            }
            for (int i = urlSafe.Length; i < totalLen; i++)
                buf[i] = '=';
            return new string(buf);
        }
        else
        {
            char[] buf = new char[totalLen];
            for (int i = 0; i < urlSafe.Length; i++)
            {
                char c = urlSafe[i];
                buf[i] = c switch
                {
                    '-' => '+',
                    '_' => '/',
                    _ => c,
                };
            }
            for (int i = urlSafe.Length; i < totalLen; i++)
                buf[i] = '=';
            return new string(buf);
        }
    }
}
