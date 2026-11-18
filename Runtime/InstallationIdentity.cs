using System;
using System.Security.Cryptography;
using System.Text;

namespace GameAnalytics.Client
{
    /// <summary>
    /// Derives a stable installation UUID from a host-supplied device key, so that a reinstall
    /// keeps the same analytics entity while two operating-system accounts on one machine stay
    /// separate. This assembly has no engine reference and cannot read a platform identifier
    /// itself: the host supplies the device string and the OS account name (for example Unity's
    /// <c>SystemInfo.deviceUniqueIdentifier</c> and <c>Environment.UserName</c>).
    ///
    /// The derivation is deterministic: the same three inputs always produce the same UUID on
    /// every launch, every build and every machine that computes them. The host still owns the
    /// decision to use it; an unsupported or empty device string means the host keeps its own
    /// persisted random installation UUID instead. A device key is a pseudonymous identifier,
    /// not an account: it survives a reinstall, it does not survive a different OS account, and
    /// one OS account shared by several players still resolves to one entity.
    /// </summary>
    public static class InstallationIdentity
    {
        /// <summary>
        /// Derives the installation UUID for an available device key. Returns false, leaving
        /// <paramref name="installationId"/> empty, when no device string was supplied — the
        /// host must then fall back to its own persisted installation UUID.
        /// </summary>
        /// <param name="deviceId">
        /// Host-supplied device identifier. Empty, whitespace or a platform's unsupported marker
        /// means the caller must not use a derived identity.
        /// </param>
        /// <param name="osUser">
        /// Host-supplied operating-system account name, or null when it is unavailable. Including
        /// it keeps two OS accounts on one machine as two entities.
        /// </param>
        /// <param name="salt">
        /// Host-supplied namespace, normally the project ID, so the same machine cannot be
        /// correlated across projects.
        /// </param>
        /// <param name="installationId">The derived UUID when the method returns true.</param>
        public static bool TryDerive(string? deviceId, string? osUser, string? salt, out Guid installationId)
        {
            installationId = default;
            if (string.IsNullOrWhiteSpace(deviceId))
                return false;

            var digest = Sha256(Material(salt, deviceId, osUser));
            var bytes = new byte[16];
            Array.Copy(digest, bytes, 16);

            // RFC 9562 version 8 (custom, name-based) with the RFC variant: the value is
            // recognisably derived rather than random and never collides with a host's v4 UUID.
            // Guid(byte[]) stores Data3 little-endian, so its most significant byte is index 7.
            bytes[7] = (byte)((bytes[7] & 0x0F) | 0x80);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);

            installationId = new Guid(bytes);
            return true;
        }

        // Length-prefixed components keep the encoding unambiguous: no separator character or
        // concatenation of values can produce the same byte sequence as a different triple.
        static byte[] Material(string? salt, string? deviceId, string? osUser)
        {
            var builder = new StringBuilder(128);
            Append(builder, salt);
            Append(builder, deviceId);
            Append(builder, osUser);
            return Encoding.UTF8.GetBytes(builder.ToString());
        }

        static void Append(StringBuilder builder, string? value)
        {
            value ??= string.Empty;
            builder.Append(value.Length).Append(':').Append(value);
        }

        // SHA256.Create() is the .NET Standard 2.0 surface; the static HashData entry point only
        // exists from .NET 5, which this library does not target.
        static byte[] Sha256(byte[] material)
        {
            using var sha = SHA256.Create();
            return sha.ComputeHash(material);
        }
    }
}
