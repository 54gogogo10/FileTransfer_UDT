using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace TrFileTransfer
{
    /// <summary>
    /// Session encryption primitives shared by the TCP and UDT transports. The 0x07
    /// handshake (see WIRE-PROTOCOL.md) agrees on a random 16-byte session salt; both
    /// sides then derive per-direction AES-256-CTR encryption keys and HMAC-SHA256
    /// MAC keys from the pairing-code hash. Everything here is C# 5 so the test build
    /// (old csc.exe) can compile it.
    /// </summary>
    #pragma warning disable 1591

    /// <summary>Key derivation: master = SHA256(salt || psk); per-purpose key =
    /// SHA256(master || purpose). PSK is the SHA256 of the pairing code — the same
    /// bytes the 0x05 frame already carries, so the raw code never crosses the wire.</summary>
    public static class SessionCrypto
    {
        public const int KeyBytes = 32;     // AES-256
        public const int MacBytes = 32;     // HMAC-SHA256 (wire carries a 16-byte truncation)
        public const int WireMacBytes = 16;
        public const int SaltBytes = 16;

        public static byte[] DeriveKey(byte[] psk, byte[] salt, string purpose)
        {
            byte[] purposeBytes = System.Text.Encoding.UTF8.GetBytes(purpose);
            using (var sha = SHA256.Create())
            {
                var buf = new byte[salt.Length + psk.Length];
                Buffer.BlockCopy(salt, 0, buf, 0, salt.Length);
                Buffer.BlockCopy(psk, 0, buf, salt.Length, psk.Length);
                byte[] master = sha.ComputeHash(buf);

                var buf2 = new byte[master.Length + purposeBytes.Length];
                Buffer.BlockCopy(master, 0, buf2, 0, master.Length);
                Buffer.BlockCopy(purposeBytes, 0, buf2, master.Length, purposeBytes.Length);
                return sha.ComputeHash(buf2);
            }
        }

        /// <summary>Derives the four keys for one session: client->server and
        /// server->client each get an encryption key and a MAC key.</summary>
        public static void DeriveSessionKeys(byte[] psk, byte[] salt,
            out byte[] c2sEnc, out byte[] c2sMac, out byte[] s2cEnc, out byte[] s2cMac)
        {
            c2sEnc = DeriveKey(psk, salt, "c2s-enc");
            c2sMac = DeriveKey(psk, salt, "c2s-mac");
            s2cEnc = DeriveKey(psk, salt, "s2c-enc");
            s2cMac = DeriveKey(psk, salt, "s2c-mac");
        }

        // ---- Authenticated ECDH handshake (0x09/0x19) --------------------------
        //
        // The pairing code is only 6 digits, so it must never be the encryption key
        // material (the previous 0x07 design sent SHA256(code) as the PSK, letting any
        // eavesdropper derive the session keys). Instead:
        //   1. both sides do an ephemeral P-256 ECDH exchange (x, y public blobs);
        //   2. the shared secret plus the code authenticate the transcript via HMAC
        //      confirms in both directions — so a party without the code (or without
        //      the private key) cannot finish the handshake;
        //   3. the session keys are derived from the DH secret (bound to the code),
        //      never from anything that crossed the wire.
        // A passive eavesdropper learns nothing usable. An ACTIVE man-in-the-middle
        // still has to brute-force the 6-digit code offline (cost raised by the
        // PBKDF2 stretch below); a longer pairing code removes that residual risk.

        /// <summary>Length of an exported P-256 ECDH public key blob (BCRYPT layout).</summary>
        public const int EcdhPublicBytes = 72;
        /// <summary>PBKDF2 iterations used to turn the pairing code into an
        /// authentication secret. It is the per-guess cost an offline attacker pays,
        /// but it also runs on both ends of every transfer, so it is kept to a value
        /// that stays imperceptible for a single file. The real lever against brute
        /// force is a longer pairing code — see the release notes.</summary>
        public const int PairingKdfIterations = 50000;

        /// <summary>Creates an ephemeral P-256 ECDH key pair.</summary>
        public static ECDiffieHellmanCng CreateEcdhKey()
        {
            return new ECDiffieHellmanCng(CngKey.Create(CngAlgorithm.ECDiffieHellmanP256));
        }

        /// <summary>Exports the public half as a fixed-size blob for the wire.</summary>
        public static byte[] EcdhPublicBlob(ECDiffieHellmanCng key)
        {
            return key.PublicKey.ToByteArray();
        }

        /// <summary>Computes the ECDH shared secret (32 bytes for P-256). Throws on a
        /// malformed peer blob rather than returning something attacker-chosen.</summary>
        public static byte[] EcdhDeriveShared(ECDiffieHellmanCng own, byte[] peerBlob)
        {
            if (peerBlob == null || peerBlob.Length != EcdhPublicBytes)
                throw new IOException("bad ECDH public key length");
            var peer = ECDiffieHellmanCngPublicKey.FromByteArray(peerBlob, CngKeyBlobFormat.EccPublicBlob);
            return own.DeriveKeyMaterial(peer);
        }

        /// <summary>Authentication key = SHA256(label || DH secret || PBKDF2(code, salt)).
        /// salt binds the transcript (both public keys) so confirms cannot be replayed
        /// into a different handshake.</summary>
        public static byte[] DeriveAuthKey(string pairingCode, byte[] clientPub, byte[] serverPub, byte[] shared)
        {
            byte[] salt;
            using (var sha = SHA256.Create())
            {
                var buf = new byte[clientPub.Length + serverPub.Length];
                Buffer.BlockCopy(clientPub, 0, buf, 0, clientPub.Length);
                Buffer.BlockCopy(serverPub, 0, buf, clientPub.Length, serverPub.Length);
                salt = sha.ComputeHash(buf);
            }

            byte[] psk;
            using (var kdf = new Rfc2898DeriveBytes(pairingCode, salt, PairingKdfIterations))
                psk = kdf.GetBytes(KeyBytes);

            byte[] label = System.Text.Encoding.UTF8.GetBytes("tf-ecdh-auth");
            using (var sha = SHA256.Create())
            {
                var buf = new byte[label.Length + shared.Length + psk.Length];
                Buffer.BlockCopy(label, 0, buf, 0, label.Length);
                Buffer.BlockCopy(shared, 0, buf, label.Length, shared.Length);
                Buffer.BlockCopy(psk, 0, buf, label.Length + shared.Length, psk.Length);
                return sha.ComputeHash(buf);
            }
        }

        /// <summary>HMAC-SHA256 confirmation over the labeled transcript.</summary>
        public static byte[] Confirm(byte[] authKey, string label, byte[] clientPub, byte[] serverPub)
        {
            byte[] labelBytes = System.Text.Encoding.UTF8.GetBytes(label);
            using (var mac = new HMACSHA256(authKey))
            {
                var buf = new byte[labelBytes.Length + clientPub.Length + serverPub.Length];
                Buffer.BlockCopy(labelBytes, 0, buf, 0, labelBytes.Length);
                Buffer.BlockCopy(clientPub, 0, buf, labelBytes.Length, clientPub.Length);
                Buffer.BlockCopy(serverPub, 0, buf, labelBytes.Length + clientPub.Length, serverPub.Length);
                return mac.ComputeHash(buf);
            }
        }
    }

    /// <summary>AES-256-CTR keystream applied in place. Sequential, single-threaded;
    /// the counter starts at zero (keys are unique per session and direction, so IV
    /// reuse across sessions cannot happen).</summary>
    internal sealed class AesCtrTransform : IDisposable
    {
        private readonly Aes _aes;
        private readonly ICryptoTransform _encryptor;
        private readonly byte[] _counter = new byte[16];
        private readonly byte[] _keystream = new byte[16];
        private int _ksPos = 16; // force generation on first use

        public AesCtrTransform(byte[] key)
        {
            _aes = Aes.Create();
            _aes.Mode = CipherMode.ECB;
            _aes.Padding = PaddingMode.None;
            _encryptor = _aes.CreateEncryptor(key, null);
        }

        /// <summary>XORs count bytes of the buffer (in place) with the keystream.</summary>
        public void Apply(byte[] buffer, int offset, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (_ksPos == 16)
                {
                    _encryptor.TransformBlock(_counter, 0, 16, _keystream, 0);
                    for (int b = 15; b >= 0; b--)
                    {
                        if (++_counter[b] != 0) break;
                    }
                    _ksPos = 0;
                }
                buffer[offset + i] ^= _keystream[_ksPos++];
            }
        }

        public void Dispose()
        {
            if (_encryptor != null) _encryptor.Dispose();
            if (_aes != null) _aes.Dispose();
        }
    }

    /// <summary>
    /// IWireStream decorator providing confidentiality + integrity over any inner
    /// stream. Wire format per segment (one direction):
    ///   legacy: [4B plainLen][16B MAC(over lenBytes || ciphertext)][ciphertext]
    ///   bound:  [4B seq][4B plainLen][16B MAC(over seq || lenBytes || ciphertext)][ciphertext]
    /// Writes larger than MaxSegment are split into multiple segments. Buffer contents
    /// passed to WriteExactAsync are destroyed (encrypted in place — the send pipeline
    /// never reuses a buffer after writing it). One concurrent reader + one concurrent
    /// writer are supported (the transfer pipeline prefetches reads during writes);
    /// their state is disjoint.
    /// </summary>
    public class EncryptedWireStream : IWireStream
    {
        /// <summary>Maximum plaintext bytes per segment.</summary>
        public const int MaxSegment = 262144;

        private readonly IWireStream _inner;
        private readonly bool _ownsInner;
        private readonly bool _bindSequence;
        private readonly AesCtrTransform _enc;
        private readonly AesCtrTransform _dec;
        private readonly HMACSHA256 _macOut;
        private readonly HMACSHA256 _macIn;
        // Scratch buffers are PER DIRECTION: the transfer pipeline overlaps one reader
        // and one writer on the same stream, so they must never share state
        private readonly byte[] _lenBufOut = new byte[4];
        private readonly byte[] _wireMacOut = new byte[SessionCrypto.WireMacBytes];
        private readonly byte[] _lenBufIn = new byte[4];
        private readonly byte[] _wireMacIn = new byte[SessionCrypto.WireMacBytes];
        private readonly byte[] _seqBufOut = new byte[8];
        private readonly byte[] _seqBufIn = new byte[8];
        // Per-direction segment counters, mixed into the MAC so equal-length ciphertext
        // segments cannot be reordered/swapped without detection.
        private long _seqOut;
        private long _seqIn;

        // Read-side decryption buffer (owned; distinct from the caller's write buffers)
        private byte[] _plain;
        private int _plainPos;
        private int _plainLen;
        private bool _disposed;

        /// <param name="keyOut">AES key for bytes this side sends.</param>
        /// <param name="macKeyOut">HMAC key for bytes this side sends.</param>
        /// <param name="keyIn">AES key for bytes this side receives.</param>
        /// <param name="macKeyIn">HMAC key for bytes this side receives.</param>
        public EncryptedWireStream(IWireStream inner, byte[] keyOut, byte[] macKeyOut,
            byte[] keyIn, byte[] macKeyIn)
            : this(inner, keyOut, macKeyOut, keyIn, macKeyIn, true, false)
        {
        }

        /// <param name="ownsInner">False when the transport owns the inner stream: the
        /// server disposes the decorator to free its own state but must not close the
        /// socket before it has sent the 1-byte application ACK.</param>
        public EncryptedWireStream(IWireStream inner, byte[] keyOut, byte[] macKeyOut,
            byte[] keyIn, byte[] macKeyIn, bool ownsInner)
            : this(inner, keyOut, macKeyOut, keyIn, macKeyIn, ownsInner, false)
        {
        }

        /// <param name="bindSequence">True for the 0x09 (v2.12+) handshake: adds a
        /// per-direction segment sequence number covered by the MAC. False for the
        /// legacy 0x07 format so ≤2.11 peers still interoperate.</param>
        public EncryptedWireStream(IWireStream inner, byte[] keyOut, byte[] macKeyOut,
            byte[] keyIn, byte[] macKeyIn, bool ownsInner, bool bindSequence)
        {
            _inner = inner;
            _ownsInner = ownsInner;
            _bindSequence = bindSequence;
            _enc = new AesCtrTransform(keyOut);
            _dec = new AesCtrTransform(keyIn);
            _macOut = new HMACSHA256(macKeyOut);
            _macIn = new HMACSHA256(macKeyIn);
        }

        public async Task WriteExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int pos = offset;
            int remaining = count;
            while (remaining > 0)
            {
                int seg = remaining > MaxSegment ? MaxSegment : remaining;

                PutInt32(_lenBufOut, seg);
                _enc.Apply(buffer, pos, seg);

                _macOut.Initialize();
                if (_bindSequence)
                {
                    PutInt64(_seqBufOut, _seqOut);
                    _macOut.TransformBlock(_seqBufOut, 0, 8, null, 0);
                }
                _macOut.TransformBlock(_lenBufOut, 0, 4, null, 0);
                _macOut.TransformBlock(buffer, pos, seg, null, 0);
                _macOut.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
                Buffer.BlockCopy(_macOut.Hash, 0, _wireMacOut, 0, SessionCrypto.WireMacBytes);

                if (_bindSequence)
                    await _inner.WriteExactAsync(_seqBufOut, 0, 8, ct).ConfigureAwait(false);
                await _inner.WriteExactAsync(_lenBufOut, 0, 4, ct).ConfigureAwait(false);
                await _inner.WriteExactAsync(_wireMacOut, 0, SessionCrypto.WireMacBytes, ct).ConfigureAwait(false);
                await _inner.WriteExactAsync(buffer, pos, seg, ct).ConfigureAwait(false);

                _seqOut++;
                pos += seg;
                remaining -= seg;
            }
        }

        public async Task ReadExactAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            int total = 0;
            while (total < count)
            {
                if (_plainPos >= _plainLen)
                    await ReadSegmentAsync(ct).ConfigureAwait(false);
                int n = Math.Min(count - total, _plainLen - _plainPos);
                Buffer.BlockCopy(_plain, _plainPos, buffer, offset + total, n);
                _plainPos += n;
                total += n;
            }
        }

        public async Task<int> ReadSomeAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            if (_plainPos >= _plainLen)
                await ReadSegmentAsync(ct).ConfigureAwait(false);
            int n = Math.Min(count, _plainLen - _plainPos);
            Buffer.BlockCopy(_plain, _plainPos, buffer, offset, n);
            _plainPos += n;
            return n;
        }

        /// <summary>Forwarded: the raw socket underneath is the one that has a timeout, and
        /// the caller must still be able to widen it (see IWireStream.SetReadTimeoutMs).</summary>
        public void SetReadTimeoutMs(int milliseconds)
        {
            _inner.SetReadTimeoutMs(milliseconds);
        }

        /// <summary>Reads, authenticates and decrypts the next segment into _plain.
        /// Throws IOException on MAC mismatch (tampering or key mismatch).</summary>
        private async Task ReadSegmentAsync(CancellationToken ct)
        {
            if (_bindSequence)
                await _inner.ReadExactAsync(_seqBufIn, 0, 8, ct).ConfigureAwait(false);
            await _inner.ReadExactAsync(_lenBufIn, 0, 4, ct).ConfigureAwait(false);
            int len = GetInt32(_lenBufIn);
            if (len < 0 || len > MaxSegment)
                throw new IOException(L.S_EncStreamCorrupt);
            await _inner.ReadExactAsync(_wireMacIn, 0, SessionCrypto.WireMacBytes, ct).ConfigureAwait(false);

            if (_plain == null || _plain.Length < len)
                _plain = new byte[MaxSegment];
            if (len > 0)
                await _inner.ReadExactAsync(_plain, 0, len, ct).ConfigureAwait(false);

            _macIn.Initialize();
            if (_bindSequence)
            {
                // A reordered/swapped segment carries the wrong seq and fails the MAC
                if (BitConverter.ToInt64(_seqBufIn, 0) != _seqIn)
                    throw new IOException(L.S_EncTampered);
                _macIn.TransformBlock(_seqBufIn, 0, 8, null, 0);
            }
            _macIn.TransformBlock(_lenBufIn, 0, 4, null, 0);
            _macIn.TransformBlock(_plain, 0, len, null, 0);
            _macIn.TransformFinalBlock(Utils.EmptyBytes, 0, 0);
            byte[] mac = _macIn.Hash;
            int diff = 0;
            for (int i = 0; i < SessionCrypto.WireMacBytes; i++)
                diff |= mac[i] ^ _wireMacIn[i];
            if (diff != 0)
                throw new IOException(L.S_EncTampered);

            if (len > 0)
                _dec.Apply(_plain, 0, len);
            _plainPos = 0;
            _plainLen = len;
            _seqIn++;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _enc.Dispose();
            _dec.Dispose();
            _macOut.Dispose();
            _macIn.Dispose();
            if (_ownsInner) _inner.Dispose();
        }

        private static void PutInt32(byte[] buf, int v)
        {
            buf[0] = (byte)v;
            buf[1] = (byte)(v >> 8);
            buf[2] = (byte)(v >> 16);
            buf[3] = (byte)(v >> 24);
        }

        private static int GetInt32(byte[] buf)
        {
            return buf[0] | (buf[1] << 8) | (buf[2] << 16) | (buf[3] << 24);
        }

        private static void PutInt64(byte[] buf, long v)
        {
            for (int i = 0; i < 8; i++) buf[i] = (byte)(v >> (8 * i));
        }
    }
}
