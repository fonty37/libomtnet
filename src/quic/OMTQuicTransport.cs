#if NET8_0_OR_GREATER
/*
* MIT License
*
* Copyright (c) 2025 Open Media Transport Contributors
*
* Permission is hereby granted, free of charge, to any person obtaining a copy
* of this software and associated documentation files (the "Software"), to deal
* in the Software without restriction, including without limitation the rights
* to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
* copies of the Software, and to permit persons to whom the Software is
* furnished to do so, subject to the following conditions:
*
* The above copyright notice and this permission notice shall be included in all
* copies or substantial portions of the Software.
*
* THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
* IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
* FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
* AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
* LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
* OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
* SOFTWARE.
*
*/

using System;
using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace libomtnet.quic
{
    /// <summary>
    /// QUIC transport constants and helpers for OMT.
    ///
    /// QUIC provides:
    ///   - No head-of-line blocking (independent streams per essence)
    ///   - Mandatory TLS 1.3 encryption
    ///   - 0-RTT / 1-RTT connection establishment
    ///   - Connection migration (survives IP changes)
    ///
    /// The OMT wire protocol (16-byte header + extended header + data) is
    /// identical over QUIC — frames are serialized into QuicStream the same
    /// way they would be into a TCP socket stream.
    ///
    /// Requires: Windows 11+ or Linux with libmsquic installed.
    /// </summary>
    public static class OMTQuicTransport
    {
        /// <summary>
        /// ALPN protocol identifier. Both sender and receiver must agree on this.
        /// </summary>
        public static readonly SslApplicationProtocol AlpnProtocol = new SslApplicationProtocol("omt");

        /// <summary>
        /// Application-level error code when a stream is aborted.
        /// Uses the OMT frame magic bytes as the code.
        /// </summary>
        public const long StreamErrorCode = 0x4F4D54; // "OMT"

        /// <summary>
        /// Application-level error code when the connection is closed.
        /// </summary>
        public const long ConnectionCloseCode = 0x4F4D5400; // "OMT\0"

        /// <summary>
        /// Default QUIC port — same as TCP default but over UDP.
        /// </summary>
        public const int DefaultPort = 6400;

        /// <summary>
        /// Check if QUIC is supported on this platform at runtime.
        /// </summary>
        public static bool IsSupported
        {
            get
            {
                try
                {
                    return QuicListener.IsSupported && QuicConnection.IsSupported;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Generate a self-signed ECDSA certificate for QUIC TLS.
        /// Used when no certificate is configured.
        /// </summary>
        public static X509Certificate2 GenerateSelfSignedCert(string cn = "CN=OMT QUIC")
        {
            using var ecdsa = ECDsa.Create();
            var req = new CertificateRequest(cn, ecdsa, HashAlgorithmName.SHA256);
            var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(5));
            return new X509Certificate2(cert.Export(X509ContentType.Pfx));
        }
    }
}
#endif
