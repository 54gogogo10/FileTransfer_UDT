using System;
using System.Security.Cryptography;
using System.Text;

namespace TrFileTransfer
{
    /// <summary>
    /// At-rest protection for short secrets stored in config.ini (the pairing code).
    /// Windows DPAPI in CurrentUser scope means another local account or a copied
    /// config file cannot simply read the code. Values are stored as
    /// "dpapi:&lt;base64&gt;"; a legacy plaintext value is still read (so an existing
    /// config keeps working) and is re-written protected on the next save.
    /// UI-layer file on purpose: System.Security (ProtectedData) is not part of the
    /// C# 5 test build's reference set.
    /// </summary>
    internal static class SecretProtector
    {
        private const string Prefix = "dpapi:";

        /// <summary>Protects a secret; returns it unchanged if DPAPI is unavailable
        /// (never turns a usable config into an unusable one).</summary>
        public static string Protect(string plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain ?? "";
            try
            {
                byte[] data = Encoding.UTF8.GetBytes(plain);
                byte[] enc = ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(enc);
            }
            catch
            {
                return plain;
            }
        }

        /// <summary>Unprotects a stored value; plaintext legacy values pass through.</summary>
        public static string Unprotect(string stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored ?? "";
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored;
            try
            {
                byte[] enc = Convert.FromBase64String(stored.Substring(Prefix.Length));
                byte[] data = ProtectedData.Unprotect(enc, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(data);
            }
            catch
            {
                // Different user/machine or corrupted value: behave as "no code" rather
                // than crashing; the user can re-enter it.
                return "";
            }
        }
    }
}
