using System.Security.Cryptography;
using System.Text;
using SmartSwitch.Core.Models;

namespace SmartSwitch.Infrastructure.Network;

internal static class PairingAuthentication
{
    private const int KeyLength = 32;
    private const int IterationCount = 200_000;

    public static byte[] DeriveKey(PairingCode pairingCode, byte[] salt) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pairingCode.Value),
            salt,
            IterationCount,
            HashAlgorithmName.SHA256,
            KeyLength);

    public static byte[] ComputeClientProof(
        byte[] key,
        byte[] clientNonce,
        byte[] challenge,
        byte[] certificateFingerprint) =>
        ComputeProof(key, "SmartSwitch/client/v1", clientNonce, challenge, certificateFingerprint);

    public static byte[] ComputeServerProof(
        byte[] key,
        byte[] clientNonce,
        byte[] challenge,
        byte[] certificateFingerprint) =>
        ComputeProof(key, "SmartSwitch/server/v1", clientNonce, challenge, certificateFingerprint);

    private static byte[] ComputeProof(byte[] key, string label, params byte[][] values)
    {
        using var hmac = new HMACSHA256(key);
        hmac.TransformBlock(
            Encoding.UTF8.GetBytes(label),
            0,
            Encoding.UTF8.GetByteCount(label),
            null,
            0);

        foreach (var value in values)
        {
            var length = BitConverter.GetBytes(value.Length);
            hmac.TransformBlock(length, 0, length.Length, null, 0);
            hmac.TransformBlock(value, 0, value.Length, null, 0);
        }

        hmac.TransformFinalBlock([], 0, 0);
        return hmac.Hash ?? throw new CryptographicException("Calcul HMAC impossible.");
    }
}
