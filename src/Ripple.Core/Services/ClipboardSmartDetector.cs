using System.Data;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Ripple.Core.Services;

public enum ClipboardSmartKind
{
    PlainText = 0,
    Url = 1,
    Email = 2,
    PhoneNumber = 3,
    HexColor = 4,
    Json = 5,
    Code = 6,
    MathExpression = 7,
    Address = 8,
    OtpCode = 9,
    FilePaths = 10,
    Image = 11
}

public sealed record ClipboardSmartAnalysis(
    ClipboardSmartKind Kind,
    string BadgeText,
    string ActionLabel,
    string? NormalizedValue = null,
    IReadOnlyList<string>? ColorPaletteHex = null,
    bool ShouldAutoExpire = false,
    TimeSpan? AutoExpireAfter = null);

public sealed record ClipboardEntry(
    string Id,
    string Text,
    ClipboardSmartAnalysis Analysis,
    DateTimeOffset CapturedAt,
    bool IsPinned,
    string SourceApp,
    string Folder = "All",
    string? ImagePath = null)
{
    public bool IsExpired(DateTimeOffset now) =>
        Analysis.ShouldAutoExpire &&
        Analysis.AutoExpireAfter is { } ttl &&
        now - CapturedAt > ttl;
}

/// <summary>
/// Classifies clipboard contents into smart actionable types (URLs, Hex colors + palette, JSON,
/// math expressions, OTP codes, emails, phone numbers, addresses, code) and provides encrypted local persistence.
/// </summary>
public static partial class ClipboardSmartDetector
{
    private static readonly Regex UrlRegex = MyUrlRegex();
    private static readonly Regex EmailRegex = MyEmailRegex();
    private static readonly Regex PhoneRegex = MyPhoneRegex();
    private static readonly Regex HexColorRegex = MyHexColorRegex();
    private static readonly Regex OtpRegex = MyOtpRegex();
    private static readonly Regex MathRegex = MyMathRegex();
    private static readonly Regex AddressRegex = MyAddressRegex();

    public static ClipboardSmartAnalysis Analyze(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new ClipboardSmartAnalysis(ClipboardSmartKind.PlainText, "TEXT", "Copy");
        }

        var trimmed = raw.Trim();

        if (OtpRegex.IsMatch(trimmed))
        {
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.OtpCode,
                "OTP CODE",
                "Paste & Auto-Clear (90s)",
                trimmed,
                ShouldAutoExpire: true,
                AutoExpireAfter: TimeSpan.FromSeconds(90));
        }

        if (HexColorRegex.IsMatch(trimmed) && (trimmed.StartsWith('#') || trimmed.Any(char.IsLetter)))
        {
            var hex = NormalizeHex(trimmed);
            var palette = BuildHarmoniousPalette(hex);
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.HexColor,
                $"COLOR {hex}",
                "Copy RGB / Palette",
                hex,
                palette);
        }

        if (UrlRegex.IsMatch(trimmed) && Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.Url,
                "URL",
                "Open in Browser",
                trimmed);
        }

        if (EmailRegex.IsMatch(trimmed))
        {
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.Email,
                "EMAIL",
                "Compose Mail",
                trimmed);
        }

        if (TryPrettifyJson(trimmed, out var prettyJson))
        {
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.Json,
                "JSON",
                "Copy Prettified",
                prettyJson);
        }

        if (TryEvaluateMath(trimmed, out var mathResult))
        {
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.MathExpression,
                $"= {mathResult}",
                $"Copy {mathResult}",
                mathResult);
        }

        if (PhoneRegex.IsMatch(trimmed))
        {
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.PhoneNumber,
                "PHONE",
                "Dial / Copy Clean",
                trimmed);
        }

        if (AddressRegex.IsMatch(trimmed))
        {
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.Address,
                "ADDRESS",
                "Open in Maps",
                trimmed);
        }

        if (LooksLikeCode(trimmed))
        {
            return new ClipboardSmartAnalysis(
                ClipboardSmartKind.Code,
                "CODE",
                "Copy as ```Block```",
                $"```\n{trimmed}\n```");
        }

        return new ClipboardSmartAnalysis(ClipboardSmartKind.PlainText, "TEXT", "Copy Plain", trimmed);
    }

    public static bool TryPrettifyJson(string text, out string formatted)
    {
        formatted = string.Empty;
        if (!(text.StartsWith('{') && text.EndsWith('}')) &&
            !(text.StartsWith('[') && text.EndsWith(']')))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            formatted = JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryEvaluateMath(string text, out string result)
    {
        result = string.Empty;
        var clean = text.Trim().TrimEnd('=').Trim();
        if (!MathRegex.IsMatch(clean) || (!clean.Contains('+') && !clean.Contains('-') && !clean.Contains('*') && !clean.Contains('/')))
        {
            return false;
        }

        try
        {
            using var dt = new DataTable();
            var val = dt.Compute(clean, null);
            if (val is IConvertible conv)
            {
                var d = conv.ToDouble(CultureInfo.InvariantCulture);
                if (!double.IsNaN(d) && !double.IsInfinity(d))
                {
                    result = Math.Abs(d - Math.Round(d)) < 1e-9
                        ? ((long)Math.Round(d)).ToString(CultureInfo.InvariantCulture)
                        : d.ToString("G8", CultureInfo.InvariantCulture);
                    return true;
                }
            }
        }
        catch
        {
            // Ignore malformed expressions
        }

        return false;
    }

    public static string NormalizeHex(string input)
    {
        var hex = input.Trim().TrimStart('#').ToUpperInvariant();
        if (hex.Length == 3)
        {
            hex = string.Concat(hex[0], hex[0], hex[1], hex[1], hex[2], hex[2]);
        }
        return $"#{hex}";
    }

    public static IReadOnlyList<string> BuildHarmoniousPalette(string normalizedHex)
    {
        if (normalizedHex.Length < 7)
        {
            return [normalizedHex];
        }

        var r = Convert.ToInt32(normalizedHex.Substring(1, 2), 16);
        var g = Convert.ToInt32(normalizedHex.Substring(3, 2), 16);
        var b = Convert.ToInt32(normalizedHex.Substring(5, 2), 16);

        string Hex(int cr, int cg, int cb) =>
            $"#{Math.Clamp(cr, 0, 255):X2}{Math.Clamp(cg, 0, 255):X2}{Math.Clamp(cb, 0, 255):X2}";

        var lighter = Hex((int)(r + (255 - r) * 0.35), (int)(g + (255 - g) * 0.35), (int)(b + (255 - b) * 0.35));
        var darker = Hex((int)(r * 0.65), (int)(g * 0.65), (int)(b * 0.65));
        var complement = Hex(255 - r, 255 - g, 255 - b);

        return [lighter, normalizedHex, darker, complement];
    }

    private static bool LooksLikeCode(string text)
    {
        if (!text.Contains('\n') && !text.Contains(';'))
        {
            return false;
        }

        string[] markers = ["function ", "const ", "let ", "var ", "class ", "public ", "private ", "def ", "import ", "return ", "=>", "{", "};"];
        var hits = markers.Count(m => text.Contains(m, StringComparison.Ordinal));
        return hits >= 2;
    }

    /// <summary>
    /// Encrypts clipboard history with AES-GCM using a user-scoped key derived via machine+user entropy.
    /// </summary>
    public static byte[] EncryptPayload(string plainJson, byte[] key)
    {
        var nonce = RandomNumberGenerator.GetBytes(AesGcm.NonceByteSizes.MaxSize);
        var plainBytes = Encoding.UTF8.GetBytes(plainJson);
        var cipherBytes = new byte[plainBytes.Length];
        var tag = new byte[AesGcm.TagByteSizes.MaxSize];

        using var aes = new AesGcm(key, AesGcm.TagByteSizes.MaxSize);
        aes.Encrypt(nonce, plainBytes, cipherBytes, tag);

        var output = new byte[nonce.Length + tag.Length + cipherBytes.Length];
        Buffer.BlockCopy(nonce, 0, output, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, output, nonce.Length, tag.Length);
        Buffer.BlockCopy(cipherBytes, 0, output, nonce.Length + tag.Length, cipherBytes.Length);
        return output;
    }

    public static string DecryptPayload(byte[] encrypted, byte[] key)
    {
        var nonceSize = AesGcm.NonceByteSizes.MaxSize;
        var tagSize = AesGcm.TagByteSizes.MaxSize;
        if (encrypted.Length < nonceSize + tagSize)
        {
            throw new CryptographicException("Invalid encrypted clipboard payload.");
        }

        var nonce = encrypted.AsSpan(0, nonceSize);
        var tag = encrypted.AsSpan(nonceSize, tagSize);
        var cipher = encrypted.AsSpan(nonceSize + tagSize);
        var plain = new byte[cipher.Length];

        using var aes = new AesGcm(key, tagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    [GeneratedRegex(@"^https?://[^\s/$.?#].[^\s]*$", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MyUrlRegex();

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.Compiled)]
    private static partial Regex MyEmailRegex();

    [GeneratedRegex(@"^\+?[\d\s\-\(\)]{7,18}$", RegexOptions.Compiled)]
    private static partial Regex MyPhoneRegex();

    [GeneratedRegex(@"^#?([0-9a-fA-F]{6}|[0-9a-fA-F]{3})$", RegexOptions.Compiled)]
    private static partial Regex MyHexColorRegex();

    [GeneratedRegex(@"^\d{6}$", RegexOptions.Compiled)]
    private static partial Regex MyOtpRegex();

    [GeneratedRegex(@"^[\d\.\+\-\*\/\(\)\s]+$", RegexOptions.Compiled)]
    private static partial Regex MyMathRegex();

    [GeneratedRegex(@"^\d{1,5}\s+[\w\s\.\-]+\s+(St|Street|Ave|Avenue|Rd|Road|Blvd|Boulevard|Ln|Lane|Dr|Drive|Way|Ct|Court)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)]
    private static partial Regex MyAddressRegex();
}
