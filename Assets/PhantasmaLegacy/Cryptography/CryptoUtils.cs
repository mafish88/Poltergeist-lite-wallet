using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Org.BouncyCastle.Asn1.Nist;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;
using Poltergeist.PhantasmaLegacy.Cryptography.ECC;

namespace Poltergeist.PhantasmaLegacy.Cryptography
{
    public static class CryptoUtils
    {
        public enum SignatureFormat
        {
            None,
            Concatenated,
            DEREncoded
        }

        public static string ToAddress(this UInt160 scriptHash)
        {
            byte[] data = new byte[21];
            data[0] = 23;
            Buffer.BlockCopy(scriptHash.ToArray(), 0, data, 1, 20);
            return data.Base58CheckEncode();
        }

        public static UInt160 ToScriptHash(this byte[] script)
        {
            return new UInt160(Hash160(script));
        }
   
        public static byte[] Hash160(byte[] message)
        {
            return message.Sha256().RIPEMD160();
        }

        public static byte[] Hash256(byte[] message)
        {
            return message.Sha256().Sha256();
        }

        // Transcodes the JCA ASN.1/DER-encoded signature into the concatenated R + S format expected by ECDSA JWS.
        private static byte[] TranscodeSignatureToConcat(byte[] derSignature, int outputLength)
        {
            if (derSignature.Length < 8 || derSignature[0] != 48) throw new Exception("Invalid ECDSA signature format");

            int offset;
            if (derSignature[1] > 0)
                offset = 2;
            else if (derSignature[1] == 0x81)
                offset = 3;
            else
                throw new Exception("Invalid ECDSA signature format");

            var rLength = derSignature[offset + 1];

            int i = rLength;
            while (i > 0
                   && derSignature[offset + 2 + rLength - i] == 0)
                i--;

            var sLength = derSignature[offset + 2 + rLength + 1];

            int j = sLength;
            while (j > 0
                   && derSignature[offset + 2 + rLength + 2 + sLength - j] == 0)
                j--;

            var rawLen = Math.Max(i, j);
            rawLen = Math.Max(rawLen, outputLength / 2);

            if ((derSignature[offset - 1] & 0xff) != derSignature.Length - offset
                || (derSignature[offset - 1] & 0xff) != 2 + rLength + 2 + sLength
                || derSignature[offset] != 2
                || derSignature[offset + 2 + rLength] != 2)
                throw new Exception("Invalid ECDSA signature format");

            var concatSignature = new byte[2 * rawLen];

            Array.Copy(derSignature, offset + 2 + rLength - i, concatSignature, rawLen - i, i);
            Array.Copy(derSignature, offset + 2 + rLength + 2 + sLength - j, concatSignature, 2 * rawLen - j, j);

            return concatSignature;
        }

        public static byte[] Sign(byte[] message, byte[] prikey, byte[] pubkey, ECDsaCurve phaCurve = ECDsaCurve.Secp256r1, SignatureFormat signatureFormat = SignatureFormat.Concatenated)
        {
            var signer = SignerUtilities.GetSigner("SHA256withECDSA");
            Org.BouncyCastle.Asn1.X9.X9ECParameters curve;
            switch (phaCurve)
            {
                case ECDsaCurve.Secp256k1:
                    curve = Org.BouncyCastle.Asn1.Sec.SecNamedCurves.GetByName("secp256k1");
                    break;
                default:
                    curve = NistNamedCurves.GetByName("P-256");
                    break;
            }
            var dom = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);
            ECKeyParameters privateKeyParameters = new ECPrivateKeyParameters(new BigInteger(1, prikey), dom);

            signer.Init(true, privateKeyParameters);
            signer.BlockUpdate(message, 0, message.Length);
            var sig = signer.GenerateSignature();

            switch (signatureFormat)
            {
                case SignatureFormat.Concatenated:
                    // We convert from default DER format that Bouncy Castle uses to concatenated "raw" R + S format.
                    return TranscodeSignatureToConcat(sig, 64);
                case SignatureFormat.DEREncoded:
                    // Return DER-encoded signature unchanged.
                    return sig;
                default:
                    throw new Exception("Unknown signature format");
            }
        }

        public static bool Verify(byte[] message, byte[] signature, byte[] pubkey, ECDsaCurve phaCurve = ECDsaCurve.Secp256r1, SignatureFormat signatureFormat = SignatureFormat.Concatenated)
        {
            var signer = SignerUtilities.GetSigner("SHA256withECDSA");
            Org.BouncyCastle.Asn1.X9.X9ECParameters curve;
            switch (phaCurve)
            {
                case ECDsaCurve.Secp256k1:
                    curve = Org.BouncyCastle.Asn1.Sec.SecNamedCurves.GetByName("secp256k1");
                    break;
                default:
                    curve = NistNamedCurves.GetByName("P-256");
                    break;
            }
            var dom = new ECDomainParameters(curve.Curve, curve.G, curve.N, curve.H);

            var point = dom.Curve.DecodePoint(pubkey);
            var publicKeyParameters = new ECPublicKeyParameters(point, dom);

            signer.Init(false, publicKeyParameters);
            signer.BlockUpdate(message, 0, message.Length);

            switch (signatureFormat)
            {
                case SignatureFormat.Concatenated:
                    // We convert from concatenated "raw" R + S format to DER format that Bouncy Castle uses.
                    signature = new Org.BouncyCastle.Asn1.DerSequence(
                        // first 32 bytes is "r" number
                        new Org.BouncyCastle.Asn1.DerInteger(new BigInteger(1, signature.Take(32).ToArray())),
                        // last 32 bytes is "s" number
                        new Org.BouncyCastle.Asn1.DerInteger(new BigInteger(1, signature.Skip(32).ToArray())))
                        .GetDerEncoded();
                    break;
                case SignatureFormat.DEREncoded:
                    // Do nothing, signature already DER-encoded.
                    break;
                default:
                    throw new Exception("Unknown signature format");
            }

            return signer.VerifySignature(signature);
        }

        private static ThreadLocal<SHA256> _sha256 = new ThreadLocal<SHA256>(() => SHA256.Create());
        private static ThreadLocal<PhantasmaLegacy.Cryptography.RIPEMD160> _ripemd160 = new ThreadLocal<PhantasmaLegacy.Cryptography.RIPEMD160>(() => new PhantasmaLegacy.Cryptography.RIPEMD160());

        public static T[] SubArray<T>(this T[] data, int index, int length)
        {
            T[] result = new T[length];
            Array.Copy(data, index, result, 0, length);
            return result;
        }

        public static byte[] AES256Decrypt(this byte[] block, byte[] key)
        {
            using (Aes aes = Aes.Create())
            {
                aes.Key = key;
                aes.Mode = CipherMode.ECB;
                aes.Padding = PaddingMode.None;
                using (ICryptoTransform decryptor = aes.CreateDecryptor())
                {
                    return decryptor.TransformFinalBlock(block, 0, block.Length);
                }
            }
        }

        public static byte[] Base58CheckDecode(this string input)
        {
            byte[] buffer = PhantasmaLegacy.Numerics.Base58.Decode(input);
            if (buffer.Length < 4) throw new FormatException();
            byte[] checksum = buffer.Sha256(0, buffer.Length - 4).Sha256();
            if (!buffer.Skip(buffer.Length - 4).SequenceEqual(checksum.Take(4)))
                throw new FormatException();
            return buffer.Take(buffer.Length - 4).ToArray();
        }

        public static string Base58CheckEncode(this byte[] data)
        {
            byte[] checksum = data.Sha256().Sha256();
            byte[] buffer = new byte[data.Length + 4];
            Buffer.BlockCopy(data, 0, buffer, 0, data.Length);
            Buffer.BlockCopy(checksum, 0, buffer, data.Length, 4);
            return PhantasmaLegacy.Numerics.Base58.Encode(buffer);
        }

        public static byte[] RIPEMD160(this IEnumerable<byte> value)
        {
            return _ripemd160.Value.ComputeHash(value.ToArray());
        }

        public static byte[] Sha256(this byte[] value, int offset, int count)
        {
            return _sha256.Value.ComputeHash(value, offset, count);
        }

        public static byte[] AddressToScriptHash(this string s)
        {
            var bytes = s.Base58CheckDecode();
            var data = bytes.Skip(1).Take(20).ToArray();
            return data;
        }
    }
}
