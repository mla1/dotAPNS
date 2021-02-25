using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using JetBrains.Annotations;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
#if !NET46
using Org.BouncyCastle.OpenSsl;
#endif

namespace dotAPNS
{
    public interface IApnsClient
    {
        [NotNull]
        [ItemNotNull]
        [Obsolete("Please use " + nameof(SendAsync) + " instead")]
        Task<ApnsResponse> Send(ApplePush push);

        [NotNull]
        [ItemNotNull]
        Task<ApnsResponse> SendAsync(ApplePush push, CancellationToken ct = default);
    }

    public class ApnsClient : IApnsClient
    {
        internal const string DevelopmentEndpoint = "https://api.development.push.apple.com";
        internal const string ProductionEndpoint = "https://api.push.apple.com";

#if NET46
        readonly CngKey _key;
#else
        readonly ECDsa _key;
#endif

        readonly ECPrivateKeyParameters _ecPrivateKeyParameter;
        readonly bool _useBouncyCastle;

        readonly string _keyId;
        readonly string _teamId;

        string _jwt;
        DateTime _lastJwtGenerationTime;
        readonly object _jwtRefreshLock = new object();

        readonly HttpClient _http;
        readonly bool _useCert;

        /// <summary>
        /// True if certificate provided can only be used for 'voip' type pushes, false otherwise.
        /// </summary>
        readonly bool _isVoipCert;

        readonly string _bundleId;
        bool _useSandbox;
        bool _useBackupPort;

        ApnsClient(HttpClient http, [NotNull] X509Certificate cert)
        {
            _http = http;
            var split = cert.Subject.Split(new[] { "0.9.2342.19200300.100.1.1=" }, StringSplitOptions.RemoveEmptyEntries);
            if (split.Length != 2)
            {
                // On Linux .NET Core cert.Subject prints `userId=xxx` instead of `0.9.2342.19200300.100.1.1=xxx`
                split = cert.Subject.Split(new[] { "userId=" }, StringSplitOptions.RemoveEmptyEntries);
            }
            if (split.Length != 2)
            {
                // if subject prints `uid=xxx` instead of `0.9.2342.19200300.100.1.1=xxx`
                split = cert.Subject.Split(new[] { "uid=" }, StringSplitOptions.RemoveEmptyEntries);
            }

            if (split.Length != 2)
                throw new InvalidOperationException("Provided certificate does not appear to be a valid APNs certificate.");

            string topic = split[1];
            _isVoipCert = topic.EndsWith(".voip");
            _bundleId = split[1].Replace(".voip", "");
            _useCert = true;
        }

        ApnsClient([NotNull] HttpClient http, [NotNull]
#if NET46 
                   CngKey
#else
                   ECDsa
#endif
                   key, [NotNull] string keyId, [NotNull] string teamId, [NotNull] string bundleId)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _key = key ?? throw new ArgumentNullException(nameof(key));

            _keyId = keyId ?? throw new ArgumentNullException(nameof(keyId),
                $"Make sure {nameof(ApnsJwtOptions)}.{nameof(ApnsJwtOptions.KeyId)} is set to a non-null value.");

            _teamId = teamId ?? throw new ArgumentNullException(nameof(teamId),
                $"Make sure {nameof(ApnsJwtOptions)}.{nameof(ApnsJwtOptions.TeamId)} is set to a non-null value.");

            _bundleId = bundleId ?? throw new ArgumentNullException(nameof(bundleId),
                $"Make sure {nameof(ApnsJwtOptions)}.{nameof(ApnsJwtOptions.BundleId)} is set to a non-null value.");
        }

        ApnsClient([NotNull] HttpClient http, [NotNull] ECPrivateKeyParameters key, [NotNull] string keyId, [NotNull] string teamId, [NotNull] string bundleId)
        {
            _http = http ?? throw new ArgumentNullException(nameof(http));
            _ecPrivateKeyParameter = key ?? throw new ArgumentNullException(nameof(key));

            _keyId = keyId ?? throw new ArgumentNullException(nameof(keyId),
                $"Make sure {nameof(ApnsJwtOptions)}.{nameof(ApnsJwtOptions.KeyId)} is set to a non-null value.");

            _teamId = teamId ?? throw new ArgumentNullException(nameof(teamId),
                $"Make sure {nameof(ApnsJwtOptions)}.{nameof(ApnsJwtOptions.TeamId)} is set to a non-null value.");

            _bundleId = bundleId ?? throw new ArgumentNullException(nameof(bundleId),
                $"Make sure {nameof(ApnsJwtOptions)}.{nameof(ApnsJwtOptions.BundleId)} is set to a non-null value.");

            _useBouncyCastle = true;
        }

        [Obsolete("Please use " + nameof(SendAsync) + " instead.")]
        public Task<ApnsResponse> Send(ApplePush push)
        {
            return SendAsync(push);
        }

        public async Task<ApnsResponse> SendAsync(ApplePush push, CancellationToken ct = default)
        {
            if (_useCert)
            {
                if (_isVoipCert && push.Type != ApplePushType.Voip)
                    throw new InvalidOperationException("Provided certificate can only be used to send 'voip' type pushes.");
            }

            var payload = push.GeneratePayload();

            string url = (_useSandbox ? DevelopmentEndpoint : ProductionEndpoint)
                + (_useBackupPort ? ":2197" : ":443")
                + "/3/device/"
                + (push.Token ?? push.VoipToken);
            var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Version = new Version(2, 0);
            req.Headers.Add("apns-priority", push.Priority.ToString());
            req.Headers.Add("apns-push-type", push.Type.ToString().ToLowerInvariant());
            req.Headers.Add("apns-topic", GetTopic(push.Type));
            if (!_useCert)
                req.Headers.Authorization = new AuthenticationHeaderValue("bearer", GetOrGenerateJwt());
            if (push.Expiration.HasValue)
            {
                var exp = push.Expiration.Value;
                if (exp == DateTimeOffset.MinValue)
                    req.Headers.Add("apns-expiration", "0");
                else
                    req.Headers.Add("apns-expiration", exp.ToUnixTimeSeconds().ToString());
            }
            if (!string.IsNullOrEmpty(push.CollapseId))
                req.Headers.Add("apns-collapse-id", push.CollapseId);
            req.Content = new JsonContent(payload);

            var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            var respContent = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);

            // Process status codes specified by APNs documentation
            // https://developer.apple.com/documentation/usernotifications/setting_up_a_remote_notification_server/handling_notification_responses_from_apns
            var statusCode = (int) resp.StatusCode;

            // Push has been successfully sent. This is the only code indicating a success as per documentation.
            if (statusCode == 200)
                return ApnsResponse.Successful();

            // something went wrong
            // check for payload 
            // {"reason":"DeviceTokenNotForTopic"}
            // {"reason":"Unregistered","timestamp":1454948015990}

            ApnsErrorResponsePayload errorPayload;
            try
            {
                errorPayload = JsonConvert.DeserializeObject<ApnsErrorResponsePayload>(respContent);
            }
            catch (JsonException ex)
            {
                return ApnsResponse.Error(ApnsResponseReason.Unknown,
                    $"Status: {statusCode}, reason: {respContent ?? "not specified"}.");
            }

            Debug.Assert(errorPayload != null);
            return ApnsResponse.Error(errorPayload.Reason, errorPayload.ReasonRaw);
        }

        public static ApnsClient CreateUsingJwt([NotNull] HttpClient http, [NotNull] ApnsJwtOptions options, bool UseBouncyCastle=false)
        {
            if (http == null) throw new ArgumentNullException(nameof(http));
            if (options == null) throw new ArgumentNullException(nameof(options));

            string certContent;
            if (options.CertFilePath != null)
            {
                Debug.Assert(options.CertContent == null);
                certContent = File.ReadAllText(options.CertFilePath);
            }
            else if (options.CertContent != null)
            {
                Debug.Assert(options.CertFilePath == null);
                certContent = options.CertContent;
            }
            else
            {
                throw new ArgumentException("Either certificate file path or certificate contents must be provided.", nameof(options));
            }

            if (UseBouncyCastle)
            {
                var pemReader = new Org.BouncyCastle.OpenSsl.PemReader(new StringReader(certContent));
                object pk = pemReader.ReadObject();

                if (pk is ECPrivateKeyParameters ecdsaPrivateKeyParameters)
                {
                    return new ApnsClient(http, ecdsaPrivateKeyParameters, options.KeyId, options.TeamId, options.BundleId);
                }
                throw new InvalidKeyException("Key must be an ECDSA private key");
            }

            certContent = certContent.Replace("\r", "").Replace("\n", "")
                .Replace("-----BEGIN PRIVATE KEY-----", "").Replace("-----END PRIVATE KEY-----", "");
#if !NET46
            certContent = $"-----BEGIN PRIVATE KEY-----\n{certContent}\n-----END PRIVATE KEY-----";
            var ecPrivateKeyParameters = (ECPrivateKeyParameters)new PemReader(new StringReader(certContent)).ReadObject();
            // See https://github.com/dotnet/core/issues/2037#issuecomment-436340605 as to why we calculate q ourselves
            // TL;DR: we don't have Q coords in ecPrivateKeyParameters, only G ones. They won't work.
            var q = ecPrivateKeyParameters.Parameters.G.Multiply(ecPrivateKeyParameters.D).Normalize();
            var d = ecPrivateKeyParameters.D.ToByteArrayUnsigned();
            var msEcp = new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q = { X = q.AffineXCoord.GetEncoded(), Y = q.AffineYCoord.GetEncoded() }, 
                D = d
            };
            var key = ECDsa.Create(msEcp);
#else
            var key = CngKey.Import(Convert.FromBase64String(certContent), CngKeyBlobFormat.Pkcs8PrivateBlob);
#endif
            return new ApnsClient(http, key, options.KeyId, options.TeamId, options.BundleId);
        }

        public static ApnsClient CreateUsingCert([NotNull] X509Certificate2 cert)
        {
#if NETSTANDARD2_0 || NET46
            throw new NotSupportedException(
                "Certificate-based connection is not supported on all .NET Framework versions and on .NET Core 2.x or lower. " +
                "For more information, see: https://github.com/alexalok/dotAPNS/issues/6");
#elif NETSTANDARD2_1
            if (cert == null) throw new ArgumentNullException(nameof(cert));

            var handler = new HttpClientHandler();
            handler.ClientCertificateOptions = ClientCertificateOption.Manual;

            handler.ClientCertificates.Add(cert);
            var client = new HttpClient(handler);

            return CreateUsingCustomHttpClient(client, cert);
#endif
        }

        public static ApnsClient CreateUsingCustomHttpClient([NotNull] HttpClient httpClient, [NotNull] X509Certificate2 cert)
        {
            if (httpClient == null) throw new ArgumentNullException(nameof(httpClient));
            if (cert == null) throw new ArgumentNullException(nameof(cert));

            var apns = new ApnsClient(httpClient, cert);
            return apns;
        }

        public static ApnsClient CreateUsingCert([NotNull] string pathToCert, string certPassword = null)
        {
            if (string.IsNullOrWhiteSpace(pathToCert))
                throw new ArgumentException("Value cannot be null or whitespace.", nameof(pathToCert));

            var cert = new X509Certificate2(pathToCert, certPassword);
            return CreateUsingCert(cert);
        }

        public ApnsClient UseSandbox()
        {
            _useSandbox = true;
            return this;
        }

        /// <summary>
        /// Use port 2197 instead of 443 to connect to the APNs server.
        /// You might use this port to allow APNs traffic through your firewall but to block other HTTPS traffic.
        /// </summary>
        /// <returns></returns>
        public ApnsClient UseBackupPort()
        {
            _useBackupPort = true;
            return this;
        }

        string GetTopic(ApplePushType pushType)
        {
            switch (pushType)
            {
                case ApplePushType.Background:
                case ApplePushType.Alert:
                    return _bundleId;
                    break;
                case ApplePushType.Voip:
                    return _bundleId + ".voip";
                case ApplePushType.Unknown:
                default:
                    throw new ArgumentOutOfRangeException(nameof(pushType), pushType, null);
            }
        }

        string GetOrGenerateJwt()
        {
            lock (_jwtRefreshLock)
            {
                return GetOrGenerateJwtInternal();
            }

            string GetOrGenerateJwtInternal()
            {
                if (_lastJwtGenerationTime > DateTime.UtcNow - TimeSpan.FromMinutes(20)) // refresh no more than once every 20 minutes
                    return _jwt;
                var now = DateTimeOffset.UtcNow;

                string header = JsonConvert.SerializeObject((new { alg = "ES256", kid = _keyId }));
                string payload = JsonConvert.SerializeObject(new { iss = _teamId, iat = now.ToUnixTimeSeconds() });

                string headerBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(header));
                string payloadBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));
                string unsignedJwtData = $"{headerBase64}.{payloadBase64}";

                byte[] signature;

                if (_useBouncyCastle)
                {
                    var sig = SignerUtilities.GetSigner("ECDSAwithSHA256");
                    sig.Init(true, _ecPrivateKeyParameter);

                    var unsignedBytes = Encoding.UTF8.GetBytes(unsignedJwtData);
                    sig.BlockUpdate(unsignedBytes, 0, unsignedBytes.Length);
                    signature = sig.GenerateSignature();
                }
                else
                {
#if NET46
                    using (var dsa = new ECDsaCng(_key))
                    {
                        dsa.HashAlgorithm = CngAlgorithm.Sha256;
                        signature = dsa.SignData(Encoding.UTF8.GetBytes(unsignedJwtData));
                    }
#else
                    signature = _key.SignData(Encoding.UTF8.GetBytes(unsignedJwtData), HashAlgorithmName.SHA256);
#endif
                }

                _jwt = $"{unsignedJwtData}.{Convert.ToBase64String(signature)}";
                _lastJwtGenerationTime = now.UtcDateTime;
                return _jwt;
            }
        }
    }
}