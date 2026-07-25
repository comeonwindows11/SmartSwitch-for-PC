using System.Globalization;
using System.Security.Cryptography;

namespace SmartSwitch.Core.Models;

public readonly record struct PairingCode
{
    public const int DigitCount = 8;

    private PairingCode(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public string DisplayValue => $"{Value[..4]}-{Value[4..]}";

    public static PairingCode Generate()
    {
        var value = RandomNumberGenerator.GetInt32(0, 100_000_000);
        return new PairingCode(value.ToString($"D{DigitCount}", CultureInfo.InvariantCulture));
    }

    public static bool TryParse(string? input, out PairingCode code)
    {
        var normalized = new string((input ?? string.Empty)
            .Where(char.IsAsciiDigit)
            .ToArray());

        if (normalized.Length != DigitCount)
        {
            code = default;
            return false;
        }

        code = new PairingCode(normalized);
        return true;
    }

    public static PairingCode Parse(string input) =>
        TryParse(input, out var code)
            ? code
            : throw new FormatException($"Le code d'association doit contenir {DigitCount} chiffres.");

    public override string ToString() => DisplayValue;
}
