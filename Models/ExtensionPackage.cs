using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Skew.Models;

internal static class ExtensionPackage
{
    private const int MaxCrxBytes = 128 * 1024 * 1024;
    private const long MaxExpandedBytes = 512L * 1024 * 1024;
    private const long MaxEntryBytes = 256L * 1024 * 1024;
    private const int MaxEntries = 20_000;

    internal static byte[] VerifyAndExtractCrx3(byte[] data, string expectedId)
    {
        if (data.Length < 16 || data.Length > MaxCrxBytes)
            throw new InvalidDataException("The downloaded CRX has an invalid size.");
        if (!data.AsSpan(0, 4).SequenceEqual("Cr24"u8))
            throw new InvalidDataException("The download is not a CRX package.");
        if (BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4, 4)) != 3)
            throw new InvalidDataException("Only signed CRX3 packages are supported.");

        int headerLength = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8, 4));
        if (headerLength <= 0 || headerLength > data.Length - 12)
            throw new InvalidDataException("The CRX3 header is invalid.");

        ReadOnlySpan<byte> header = data.AsSpan(12, headerLength);
        ReadOnlySpan<byte> archive = data.AsSpan(12 + headerLength);
        if (archive.Length < 4 || archive[0] != (byte)'P' || archive[1] != (byte)'K')
            throw new InvalidDataException("The CRX3 archive is missing.");

        byte[]? signedHeader = null;
        var proofs = new List<(byte[] Key, byte[] Signature, bool Ecdsa)>();
        foreach (var field in ReadFields(header))
        {
            if (field.Number == 10_000 && field.WireType == 2)
                signedHeader = field.Data.ToArray();
            else if ((field.Number == 2 || field.Number == 3) && field.WireType == 2)
            {
                byte[]? key = null;
                byte[]? signature = null;
                foreach (var proofField in ReadFields(field.Data))
                {
                    if (proofField.Number == 1 && proofField.WireType == 2) key = proofField.Data.ToArray();
                    if (proofField.Number == 2 && proofField.WireType == 2) signature = proofField.Data.ToArray();
                }
                if (key is not null && signature is not null)
                    proofs.Add((key, signature, field.Number == 3));
            }
        }

        if (signedHeader is null)
            throw new InvalidDataException("The CRX3 signed header is missing.");

        var idField = ReadFields(signedHeader)
            .FirstOrDefault(field => field.Number == 1 && field.WireType == 2);
        byte[]? crxId = idField.Data;
        if (crxId is null || crxId.Length != 16 || EncodeExtensionId(crxId) != expectedId)
            throw new InvalidDataException("The CRX3 extension ID does not match the requested extension.");

        byte[] signedPayload = BuildSignedPayload(signedHeader, archive);
        bool verified = proofs.Any(proof =>
            EncodeExtensionId(SHA256.HashData(proof.Key).AsSpan(0, 16)) == expectedId &&
            VerifyProof(proof.Key, proof.Signature, proof.Ecdsa, signedPayload));
        if (!verified)
            throw new CryptographicException("The Chrome Web Store signature could not be verified.");

        return archive.ToArray();
    }

    internal static void ExtractArchive(byte[] archive, string destination)
    {
        Directory.CreateDirectory(destination);
        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        long total = 0;

        using var stream = new MemoryStream(archive, writable: false);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (zip.Entries.Count == 0 || zip.Entries.Count > MaxEntries)
            throw new InvalidDataException("The extension archive contains an invalid number of files.");

        foreach (var entry in zip.Entries)
        {
            if (entry.Length > MaxEntryBytes || total > MaxExpandedBytes - entry.Length)
                throw new InvalidDataException("The extension archive is too large when extracted.");
            total += entry.Length;

            int unixType = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixType == 0xA000)
                throw new InvalidDataException("Symbolic links are not allowed in extension packages.");

            string target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The extension archive contains an unsafe path.");

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var input = entry.Open();
            using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            input.CopyTo(output);
        }
    }

    internal static string StableUnpackedId(string sourceFolder, ManifestMeta manifest)
    {
        byte[] identity;
        if (!string.IsNullOrWhiteSpace(manifest.Key))
        {
            try { identity = Convert.FromBase64String(manifest.Key); }
            catch (FormatException) { identity = Encoding.UTF8.GetBytes(Path.GetFullPath(sourceFolder).ToUpperInvariant()); }
        }
        else
        {
            identity = Encoding.UTF8.GetBytes(Path.GetFullPath(sourceFolder).ToUpperInvariant());
        }
        return EncodeExtensionId(SHA256.HashData(identity).AsSpan(0, 16));
    }

    internal static string? IdFromManifestKey(string key)
    {
        try
        {
            byte[] publicKey = Convert.FromBase64String(key);
            return EncodeExtensionId(SHA256.HashData(publicKey).AsSpan(0, 16));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool VerifyProof(byte[] key, byte[] signature, bool ecdsa, byte[] payload)
    {
        try
        {
            if (ecdsa)
            {
                using var algorithm = ECDsa.Create();
                algorithm.ImportSubjectPublicKeyInfo(key, out _);
                return algorithm.VerifyData(payload, signature, HashAlgorithmName.SHA256,
                    DSASignatureFormat.Rfc3279DerSequence);
            }

            using var rsa = RSA.Create();
            rsa.ImportSubjectPublicKeyInfo(key, out _);
            return rsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static byte[] BuildSignedPayload(byte[] signedHeader, ReadOnlySpan<byte> archive)
    {
        byte[] prefix = Encoding.ASCII.GetBytes("CRX3 SignedData\0");
        byte[] payload = new byte[prefix.Length + 4 + signedHeader.Length + archive.Length];
        prefix.CopyTo(payload, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(prefix.Length, 4), (uint)signedHeader.Length);
        signedHeader.CopyTo(payload, prefix.Length + 4);
        archive.CopyTo(payload.AsSpan(prefix.Length + 4 + signedHeader.Length));
        return payload;
    }

    private static string EncodeExtensionId(ReadOnlySpan<byte> bytes)
    {
        var chars = new char[bytes.Length * 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            chars[i * 2] = (char)('a' + (bytes[i] >> 4));
            chars[i * 2 + 1] = (char)('a' + (bytes[i] & 0x0F));
        }
        return new string(chars);
    }

    private static List<ProtoField> ReadFields(ReadOnlySpan<byte> message)
    {
        var result = new List<ProtoField>();
        int offset = 0;
        while (offset < message.Length)
        {
            ulong tag = ReadVarint(message, ref offset);
            int fieldNumber = checked((int)(tag >> 3));
            int wireType = (int)(tag & 7);
            if (fieldNumber <= 0) throw new InvalidDataException("Invalid protobuf field.");

            if (wireType == 2)
            {
                int length = checked((int)ReadVarint(message, ref offset));
                if (length < 0 || length > message.Length - offset)
                    throw new InvalidDataException("Invalid protobuf length.");
                result.Add(new ProtoField(fieldNumber, wireType, message.Slice(offset, length).ToArray()));
                offset += length;
            }
            else if (wireType == 0)
            {
                ReadVarint(message, ref offset);
                result.Add(new ProtoField(fieldNumber, wireType, Array.Empty<byte>()));
            }
            else if (wireType == 1)
            {
                if (message.Length - offset < 8) throw new InvalidDataException("Invalid protobuf field.");
                offset += 8;
                result.Add(new ProtoField(fieldNumber, wireType, Array.Empty<byte>()));
            }
            else if (wireType == 5)
            {
                if (message.Length - offset < 4) throw new InvalidDataException("Invalid protobuf field.");
                offset += 4;
                result.Add(new ProtoField(fieldNumber, wireType, Array.Empty<byte>()));
            }
            else
            {
                throw new InvalidDataException("Unsupported protobuf wire type.");
            }
        }
        return result;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong value = 0;
        for (int shift = 0; shift < 64; shift += 7)
        {
            if (offset >= data.Length) throw new InvalidDataException("Truncated protobuf value.");
            byte next = data[offset++];
            value |= (ulong)(next & 0x7F) << shift;
            if ((next & 0x80) == 0) return value;
        }
        throw new InvalidDataException("Invalid protobuf value.");
    }

    private readonly record struct ProtoField(int Number, int WireType, byte[] Data);
}
