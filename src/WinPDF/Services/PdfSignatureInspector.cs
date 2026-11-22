using System.Buffers;
using System.Formats.Asn1;
using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using WinPDF.Models;

namespace WinPDF.Services;

/// <summary>
/// Pure .NET 10 offline cryptographic verifier and metadata extractor for PDF PKCS#7 / CAdES digital signatures.
/// Runs with zero managed LOH allocations.
/// </summary>
internal static class PdfSignatureInspector
{
    private const string OidSignedData = "1.2.840.113549.1.7.2";
    private const string OidMessageDigest = "1.2.840.113549.1.9.4";
    private const string OidSigningTime = "1.2.840.113549.1.9.5";
    private const string OidSha1 = "1.3.14.3.2.26";
    private const string OidSha256 = "2.16.840.1.101.3.4.2.1";
    private const string OidSha384 = "2.16.840.1.101.3.4.2.2";
    private const string OidSha512 = "2.16.840.1.101.3.4.2.3";
    private const string OidRsaEncryption = "1.2.840.113549.1.1.1";
    private const string OidRsaSha1 = "1.2.840.113549.1.1.5";
    private const string OidRsaSha256 = "1.2.840.113549.1.1.11";
    private const string OidRsaSha384 = "1.2.840.113549.1.1.12";
    private const string OidRsaSha512 = "1.2.840.1.101.3.4.1.13"; // legacy alias fallback below too
    private const string OidRsaSha512Pkcs = "1.2.840.113549.1.1.13";
    private const string OidRsaPss = "1.2.840.113549.1.1.10";
    private const string OidEcdsaSha1 = "1.2.840.10045.4.1";
    private const string OidEcdsaSha256 = "1.2.840.10045.4.3.2";
    private const string OidEcdsaSha384 = "1.2.840.10045.4.3.3";
    private const string OidEcdsaSha512 = "1.2.840.10045.4.3.4";

    /// <summary>
    /// Inspects and cryptographically verifies a single PDF signature against the file bytes defined by <paramref name="byteRange"/>.
    /// </summary>
    internal static PdfSignatureInfo InspectSignature(
        int index,
        string filePath,
        long fileLength,
        byte[] rawContents,
        int[] byteRange,
        string subFilter,
        string reason,
        string rawPdfTime,
        uint docMdpPermission)
    {
        DateTimeOffset? declaredTime = ParsePdfDate(rawPdfTime);
        bool coversWholeDoc = CoversFullFile(byteRange, fileLength);

        if (rawContents.Length == 0 ||
            !AsnDecoder.TryReadEncodedValue(rawContents, AsnEncodingRules.BER, out _, out _, out _, out int consumedBytes) ||
            consumedBytes <= 0)
        {
            return new PdfSignatureInfo(
                Index: index,
                SignerName: string.Empty,
                SignerDistinguishedName: string.Empty,
                IssuerName: string.Empty,
                SigningTime: declaredTime,
                Reason: reason,
                SubFilter: subFilter,
                DocMdpPermission: docMdpPermission,
                CoversWholeDocument: coversWholeDoc,
                IsCryptographicallyValid: false,
                IsCertificateChainTrusted: false,
                Status: PdfSignatureVerificationStatus.Unknown,
                StatusDetails: "Unsupported or empty signature payload.",
                CertNotBefore: null,
                CertNotAfter: null,
                CertThumbprint: string.Empty,
                CertSerialNumber: string.Empty);
        }

        List<X509Certificate2> certs = new();
        try
        {
            ReadOnlyMemory<byte> cmsBytes = rawContents.AsMemory(0, consumedBytes);
            AsnReader rootReader = new(cmsBytes, AsnEncodingRules.BER);
            AsnReader contentInfo = rootReader.ReadSequence();
            string contentType = contentInfo.ReadObjectIdentifier();
            if (!string.Equals(contentType, OidSignedData, StringComparison.Ordinal))
            {
                return CreateUnknown(index, declaredTime, reason, subFilter, docMdpPermission, coversWholeDoc, $"Unsupported CMS ContentType: {contentType}");
            }

            AsnReader explicitContent = contentInfo.ReadSequence(new Asn1Tag(TagClass.ContextSpecific, 0, isConstructed: true));
            AsnReader signedData = explicitContent.ReadSequence();

            _ = signedData.ReadInteger(); // version
            _ = signedData.ReadEncodedValue(); // digestAlgorithms SET
            _ = signedData.ReadEncodedValue(); // encapContentInfo SEQUENCE

            Asn1Tag certsTag = new(TagClass.ContextSpecific, 0, isConstructed: true);
            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(certsTag))
            {
                AsnReader certsReader = signedData.ReadSetOf(certsTag);
                while (certsReader.HasData)
                {
                    if (certsReader.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                    {
                        ReadOnlyMemory<byte> certDer = certsReader.ReadEncodedValue();
                        try
                        {
                            certs.Add(X509CertificateLoader.LoadCertificate(certDer.Span));
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        _ = certsReader.ReadEncodedValue();
                    }
                }
            }

            Asn1Tag crlsTag = new(TagClass.ContextSpecific, 1, isConstructed: true);
            if (signedData.HasData && signedData.PeekTag().HasSameClassAndValue(crlsTag))
            {
                _ = signedData.ReadEncodedValue();
            }

            AsnReader signerInfos = signedData.ReadSetOf();
            if (!signerInfos.HasData)
            {
                return CreateUnknown(index, declaredTime, reason, subFilter, docMdpPermission, coversWholeDoc, "No SignerInfo entry present.");
            }

            AsnReader si = signerInfos.ReadSequence();
            _ = si.ReadInteger(); // SignerInfo version

            byte[]? issuerRaw = null;
            BigInteger? targetSerial = null;
            byte[]? targetSki = null;

            if (si.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
            {
                AsnReader ias = si.ReadSequence();
                issuerRaw = ias.ReadEncodedValue().ToArray();
                targetSerial = new BigInteger(ias.ReadIntegerBytes().Span, isUnsigned: true, isBigEndian: true);
            }
            else if (si.PeekTag().HasSameClassAndValue(new Asn1Tag(TagClass.ContextSpecific, 0)))
            {
                targetSki = si.ReadOctetString(new Asn1Tag(TagClass.ContextSpecific, 0));
            }
            else
            {
                _ = si.ReadEncodedValue();
            }

            AsnReader digestAlgSeq = si.ReadSequence();
            string digestOid = digestAlgSeq.ReadObjectIdentifier();
            HashAlgorithmName digestAlg = MapDigestOid(digestOid);

            byte[]? expectedMessageDigest = null;
            DateTimeOffset? cmsSigningTime = null;
            byte[]? attrsForVerification = null;

            Asn1Tag signedAttrsTag = new(TagClass.ContextSpecific, 0, isConstructed: true);
            if (si.HasData && si.PeekTag().HasSameClassAndValue(signedAttrsTag))
            {
                ReadOnlyMemory<byte> rawSignedAttrs = si.ReadEncodedValue();
                attrsForVerification = rawSignedAttrs.ToArray();
                if (attrsForVerification.Length > 0)
                {
                    attrsForVerification[0] = 0x31; // EXPLICIT/IMPLICIT [0] -> Universal SET tag for verification
                }

                AsnReader attrSetReader = new(attrsForVerification, AsnEncodingRules.BER);
                AsnReader attrs = attrSetReader.ReadSetOf();
                while (attrs.HasData)
                {
                    AsnReader attr = attrs.ReadSequence();
                    string attrOid = attr.ReadObjectIdentifier();
                    AsnReader valSet = attr.ReadSetOf();

                    if (string.Equals(attrOid, OidMessageDigest, StringComparison.Ordinal) && valSet.HasData)
                    {
                        expectedMessageDigest = valSet.ReadOctetString();
                    }
                    else if (string.Equals(attrOid, OidSigningTime, StringComparison.Ordinal) && valSet.HasData)
                    {
                        Asn1Tag t = valSet.PeekTag();
                        if (t.TagValue == (int)UniversalTagNumber.UtcTime)
                        {
                            cmsSigningTime = valSet.ReadUtcTime();
                        }
                        else if (t.TagValue == (int)UniversalTagNumber.GeneralizedTime)
                        {
                            cmsSigningTime = valSet.ReadGeneralizedTime();
                        }
                    }
                }
            }

            AsnReader sigAlgSeq = si.ReadSequence();
            string sigAlgOid = sigAlgSeq.ReadObjectIdentifier();
            RSASignaturePadding rsaPadding = RSASignaturePadding.Pkcs1;
            HashAlgorithmName effectiveSigHashAlg = MapSignatureHashOid(sigAlgOid, digestAlg);
            if (string.Equals(sigAlgOid, OidRsaPss, StringComparison.Ordinal))
            {
                rsaPadding = RSASignaturePadding.Pss;
                if (sigAlgSeq.HasData && sigAlgSeq.PeekTag().HasSameClassAndValue(Asn1Tag.Sequence))
                {
                    try
                    {
                        AsnReader pssParams = sigAlgSeq.ReadSequence();
                        Asn1Tag pssHashTag = new(TagClass.ContextSpecific, 0, isConstructed: true);
                        if (pssParams.HasData && pssParams.PeekTag().HasSameClassAndValue(pssHashTag))
                        {
                            AsnReader hashWrapper = pssParams.ReadSequence(pssHashTag);
                            AsnReader innerHash = hashWrapper.ReadSequence();
                            effectiveSigHashAlg = MapDigestOid(innerHash.ReadObjectIdentifier());
                        }
                    }
                    catch
                    {
                    }
                }
            }

            byte[] signatureBytes = si.ReadOctetString();

            X509Certificate2? signerCert = MatchSignerCertificate(certs, issuerRaw, targetSerial, targetSki);
            if (signerCert is null)
            {
                return CreateUnknown(index, cmsSigningTime ?? declaredTime, reason, subFilter, docMdpPermission, coversWholeDoc, "Signer certificate not found in CMS payload.");
            }

            string signerName = signerCert.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrWhiteSpace(signerName))
            {
                signerName = signerCert.Subject;
            }

            string issuerName = signerCert.GetNameInfo(X509NameType.SimpleName, forIssuer: true);
            if (string.IsNullOrWhiteSpace(issuerName))
            {
                issuerName = signerCert.Issuer;
            }

            bool digestMatches = false;
            byte[]? computedPdfHash = ComputeByteRangeHash(filePath, byteRange, digestAlg);
            if (computedPdfHash is not null && expectedMessageDigest is not null)
            {
                digestMatches = computedPdfHash.AsSpan().SequenceEqual(expectedMessageDigest);
            }
            else if (computedPdfHash is not null && attrsForVerification is null)
            {
                digestMatches = true; // direct signature over document hash without signedAttrs
            }

            bool signatureMatches = VerifyCmsSignature(
                signerCert,
                attrsForVerification,
                computedPdfHash,
                signatureBytes,
                effectiveSigHashAlg,
                sigAlgOid,
                rsaPadding);

            bool cryptoValid = digestMatches && signatureMatches;

            bool chainTrusted = false;
            string chainStatusDetail = string.Empty;
            DateTimeOffset effectiveSignTime = cmsSigningTime ?? declaredTime ?? DateTimeOffset.Now;
            try
            {
                using X509Chain chain = new();
                foreach (X509Certificate2 embedded in certs)
                {
                    chain.ChainPolicy.ExtraStore.Add(embedded);
                }

                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.VerificationTime = effectiveSignTime.UtcDateTime;
                chainTrusted = chain.Build(signerCert);
                if (!chainTrusted && chain.ChainStatus.Length > 0)
                {
                    chainStatusDetail = string.Join(", ", chain.ChainStatus.Select(s => s.StatusInformation.Trim()).Distinct().Take(2));
                }
            }
            catch (Exception ex)
            {
                chainStatusDetail = ex.Message;
            }

            PdfSignatureVerificationStatus status;
            string statusDetails;
            if (!cryptoValid)
            {
                status = PdfSignatureVerificationStatus.Invalid;
                statusDetails = digestMatches
                    ? "CMS cryptographic signature check failed."
                    : "Signed byte range hash mismatch — file content was altered.";
            }
            else if (!coversWholeDoc)
            {
                status = PdfSignatureVerificationStatus.ModifiedAfterSigning;
                statusDetails = "Signature is intact over its signed revision, but data or annotations were appended later.";
            }
            else if (chainTrusted)
            {
                status = PdfSignatureVerificationStatus.Valid;
                statusDetails = "Signature is valid and the certificate chain is trusted.";
            }
            else
            {
                status = PdfSignatureVerificationStatus.ValidUntrustedRoot;
                statusDetails = string.IsNullOrWhiteSpace(chainStatusDetail)
                    ? "Document integrity verified, but the issuing root authority is not in the Windows Trusted Root store."
                    : $"Document integrity verified ({chainStatusDetail})";
            }

            string thumbprint = Convert.ToHexString(signerCert.GetCertHash(HashAlgorithmName.SHA256));

            return new PdfSignatureInfo(
                Index: index,
                SignerName: signerName,
                SignerDistinguishedName: signerCert.Subject,
                IssuerName: issuerName,
                SigningTime: cmsSigningTime ?? declaredTime,
                Reason: reason,
                SubFilter: subFilter,
                DocMdpPermission: docMdpPermission,
                CoversWholeDocument: coversWholeDoc,
                IsCryptographicallyValid: cryptoValid,
                IsCertificateChainTrusted: chainTrusted,
                Status: status,
                StatusDetails: statusDetails,
                CertNotBefore: signerCert.NotBefore,
                CertNotAfter: signerCert.NotAfter,
                CertThumbprint: thumbprint,
                CertSerialNumber: signerCert.SerialNumber);
        }
        catch (Exception ex)
        {
            return CreateUnknown(index, declaredTime, reason, subFilter, docMdpPermission, coversWholeDoc, ex.Message);
        }
        finally
        {
            for (int i = 0; i < certs.Count; i++)
            {
                certs[i].Dispose();
            }
        }
    }

    /// <summary>
    /// Parses a standard PDF date string (<c>D:YYYYMMDDHHmmSS+HH'mm'</c>) into a <see cref="DateTimeOffset"/>.
    /// </summary>
    internal static DateTimeOffset? ParsePdfDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        ReadOnlySpan<char> s = raw.AsSpan().Trim();
        if (s.StartsWith("D:", StringComparison.Ordinal))
        {
            s = s[2..];
        }

        if (s.Length < 4 || !int.TryParse(s[..4], NumberStyles.None, CultureInfo.InvariantCulture, out int year))
        {
            return null;
        }

        int month = s.Length >= 6 && int.TryParse(s.Slice(4, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int mo) ? mo : 1;
        int day = s.Length >= 8 && int.TryParse(s.Slice(6, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int dy) ? dy : 1;
        int hour = s.Length >= 10 && int.TryParse(s.Slice(8, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int hr) ? hr : 0;
        int minute = s.Length >= 12 && int.TryParse(s.Slice(10, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int mn) ? mn : 0;
        int second = s.Length >= 14 && int.TryParse(s.Slice(12, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int sc) ? sc : 0;

        TimeSpan offset = TimeSpan.Zero;
        if (s.Length > 14)
        {
            ReadOnlySpan<char> tz = s[14..];
            char signChar = tz[0];
            if (signChar is '+' or '-')
            {
                int sign = signChar == '-' ? -1 : 1;
                int tzH = 0;
                int tzM = 0;
                int digitsPos = 1;
                if (tz.Length >= digitsPos + 2 && int.TryParse(tz.Slice(digitsPos, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedH))
                {
                    tzH = parsedH;
                    digitsPos += 2;
                    while (digitsPos < tz.Length && !char.IsAsciiDigit(tz[digitsPos]))
                    {
                        digitsPos++;
                    }

                    if (tz.Length >= digitsPos + 2 && int.TryParse(tz.Slice(digitsPos, 2), NumberStyles.None, CultureInfo.InvariantCulture, out int parsedM))
                    {
                        tzM = parsedM;
                    }
                }

                offset = TimeSpan.FromMinutes(sign * ((tzH * 60) + tzM));
            }
        }

        try
        {
            return new DateTimeOffset(
                Math.Clamp(year, 1, 9999),
                Math.Clamp(month, 1, 12),
                Math.Clamp(day, 1, DateTime.DaysInMonth(Math.Clamp(year, 1, 9999), Math.Clamp(month, 1, 12))),
                Math.Clamp(hour, 0, 23),
                Math.Clamp(minute, 0, 59),
                Math.Clamp(second, 0, 59),
                offset);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Formats a <see cref="DateTimeOffset"/> into PDF date format <c>D:YYYYMMDDHHmmSS+HH'mm'</c>.
    /// </summary>
    internal static string FormatPdfDate(DateTimeOffset dt)
    {
        TimeSpan offset = dt.Offset;
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        TimeSpan absOffset = offset.Duration();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"D:{dt:yyyyMMddHHmmss}{sign}{absOffset.Hours:00}'{absOffset.Minutes:00}'");
    }

    private static PdfSignatureInfo CreateUnknown(
        int index,
        DateTimeOffset? signingTime,
        string reason,
        string subFilter,
        uint docMdp,
        bool coversWholeDoc,
        string details)
    {
        return new PdfSignatureInfo(
            Index: index,
            SignerName: string.Empty,
            SignerDistinguishedName: string.Empty,
            IssuerName: string.Empty,
            SigningTime: signingTime,
            Reason: reason,
            SubFilter: subFilter,
            DocMdpPermission: docMdp,
            CoversWholeDocument: coversWholeDoc,
            IsCryptographicallyValid: false,
            IsCertificateChainTrusted: false,
            Status: PdfSignatureVerificationStatus.Unknown,
            StatusDetails: details,
            CertNotBefore: null,
            CertNotAfter: null,
            CertThumbprint: string.Empty,
            CertSerialNumber: string.Empty);
    }

    private static bool CoversFullFile(int[] byteRange, long fileLength)
    {
        if (byteRange.Length < 4 || fileLength <= 0)
        {
            return false;
        }

        long end = (long)byteRange[^2] + byteRange[^1];
        return end >= fileLength - 2 && end <= fileLength + 2;
    }

    private static X509Certificate2? MatchSignerCertificate(
        List<X509Certificate2> certs,
        byte[]? issuerRaw,
        BigInteger? targetSerial,
        byte[]? targetSki)
    {
        if (certs.Count == 0)
        {
            return null;
        }

        if (issuerRaw is not null && targetSerial is not null)
        {
            for (int i = 0; i < certs.Count; i++)
            {
                X509Certificate2 cert = certs[i];
                if (!cert.IssuerName.RawData.AsSpan().SequenceEqual(issuerRaw))
                {
                    continue;
                }

                BigInteger certSerial = new(cert.SerialNumberBytes.Span, isUnsigned: true, isBigEndian: true);
                if (certSerial == targetSerial.Value)
                {
                    return cert;
                }
            }
        }

        if (targetSki is not null)
        {
            for (int i = 0; i < certs.Count; i++)
            {
                X509Certificate2 cert = certs[i];
                foreach (X509Extension ext in cert.Extensions)
                {
                    if (ext is X509SubjectKeyIdentifierExtension skiExt &&
                        skiExt.SubjectKeyIdentifierBytes.Span.SequenceEqual(targetSki))
                    {
                        return cert;
                    }
                }
            }
        }

        for (int i = 0; i < certs.Count; i++)
        {
            bool isCa = false;
            foreach (X509Extension ext in certs[i].Extensions)
            {
                if (ext is X509BasicConstraintsExtension bc && bc.CertificateAuthority)
                {
                    isCa = true;
                    break;
                }
            }

            if (!isCa)
            {
                return certs[i];
            }
        }

        return certs[0];
    }

    private static byte[]? ComputeByteRangeHash(string filePath, int[] byteRange, HashAlgorithmName alg)
    {
        if (string.IsNullOrWhiteSpace(filePath) || byteRange.Length < 4 || (byteRange.Length & 1) != 0 || !File.Exists(filePath))
        {
            return null;
        }

        const int ChunkSize = 16 * 1024;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(ChunkSize);
        try
        {
            using IncrementalHash hasher = IncrementalHash.CreateHash(alg);
            using FileStream fs = new(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize, FileOptions.SequentialScan);
            long fileLen = fs.Length;

            for (int i = 0; i < byteRange.Length; i += 2)
            {
                long offset = byteRange[i];
                long remaining = byteRange[i + 1];
                if (offset < 0 || remaining < 0 || offset + remaining > fileLen)
                {
                    return null;
                }

                fs.Position = offset;
                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(remaining, ChunkSize);
                    int read = fs.Read(buffer, 0, toRead);
                    if (read <= 0)
                    {
                        return null;
                    }

                    hasher.AppendData(buffer, 0, read);
                    remaining -= read;
                }
            }

            return hasher.GetHashAndReset();
        }
        catch
        {
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static bool VerifyCmsSignature(
        X509Certificate2 cert,
        byte[]? signedAttrsDer,
        byte[]? pdfHash,
        byte[] signatureBytes,
        HashAlgorithmName hashAlg,
        string sigAlgOid,
        RSASignaturePadding rsaPadding)
    {
        try
        {
            if (sigAlgOid.StartsWith("1.2.840.10045.4", StringComparison.Ordinal))
            {
                using ECDsa? ecdsa = cert.GetECDsaPublicKey();
                if (ecdsa is null)
                {
                    return false;
                }

                if (signedAttrsDer is not null)
                {
                    return ecdsa.VerifyData(signedAttrsDer, signatureBytes, hashAlg, DSASignatureFormat.Rfc3279DerSequence);
                }

                return pdfHash is not null && ecdsa.VerifyHash(pdfHash, signatureBytes, DSASignatureFormat.Rfc3279DerSequence);
            }

            using RSA? rsa = cert.GetRSAPublicKey();
            if (rsa is not null)
            {
                if (signedAttrsDer is not null)
                {
                    return rsa.VerifyData(signedAttrsDer, signatureBytes, hashAlg, rsaPadding);
                }

                return pdfHash is not null && rsa.VerifyHash(pdfHash, signatureBytes, hashAlg, rsaPadding);
            }

            using ECDsa? fallbackEcdsa = cert.GetECDsaPublicKey();
            if (fallbackEcdsa is not null)
            {
                if (signedAttrsDer is not null)
                {
                    return fallbackEcdsa.VerifyData(signedAttrsDer, signatureBytes, hashAlg, DSASignatureFormat.Rfc3279DerSequence);
                }

                return pdfHash is not null && fallbackEcdsa.VerifyHash(pdfHash, signatureBytes, DSASignatureFormat.Rfc3279DerSequence);
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static HashAlgorithmName MapDigestOid(string oid) => oid switch
    {
        OidSha1 => HashAlgorithmName.SHA1,
        OidSha256 => HashAlgorithmName.SHA256,
        OidSha384 => HashAlgorithmName.SHA384,
        OidSha512 => HashAlgorithmName.SHA512,
        _ => HashAlgorithmName.SHA256,
    };

    private static HashAlgorithmName MapSignatureHashOid(string sigOid, HashAlgorithmName fallback) => sigOid switch
    {
        OidRsaSha1 or OidEcdsaSha1 => HashAlgorithmName.SHA1,
        OidRsaSha256 or OidEcdsaSha256 => HashAlgorithmName.SHA256,
        OidRsaSha384 or OidEcdsaSha384 => HashAlgorithmName.SHA384,
        OidRsaSha512 or OidRsaSha512Pkcs or OidEcdsaSha512 => HashAlgorithmName.SHA512,
        OidRsaEncryption => fallback,
        _ => fallback,
    };
}
