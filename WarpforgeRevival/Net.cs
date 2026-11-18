using System;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace WarpforgeRevival
{
    /// <summary>
    /// Every web client the mod uses comes from here, so they all check the server's certificate
    /// the same way.
    ///
    /// On a phone the loader's .NET cannot read Android's list of trusted certificate authorities,
    /// so every https certificate fails with "UntrustedRoot" even when it is perfectly valid. When
    /// that is the only problem, the certificate is checked again against the root authorities
    /// that free certificates come from (Let's Encrypt, ZeroSSL, Google), carried in the mod. A
    /// certificate for the wrong name, an expired one, or one from anywhere else is still refused.
    /// </summary>
    internal static class Net
    {
        // All web clients share one set of connections (one for plain replies, one for gzip ones), at
        // most MaxConnections open to the server at a time; more requests at once wait their turn.
        // Each client used to keep its own connections, so one game could hold well over a dozen
        // open, and the server serves at most a few dozen per address (a household shares one).
        private const int MaxConnections = 8;
        private static SocketsHttpHandler plain, zipped;
        private static readonly object Gate = new object();

        private static SocketsHttpHandler Handler(bool gzip)
        {
            lock (Gate)
            {
                ref SocketsHttpHandler h = ref gzip ? ref zipped : ref plain;
                if (h != null) return h;
                // A connection left open between requests is closed by the server after 30 seconds of
                // quiet. Reusing one that is already closed fails at once ("An error occurred while
                // sending the request"), so idle connections are let go well before that.
                h = new SocketsHttpHandler
                {
                    PooledConnectionIdleTimeout = TimeSpan.FromSeconds(15),
                    MaxConnectionsPerServer = MaxConnections,
                    SslOptions = new SslClientAuthenticationOptions { RemoteCertificateValidationCallback = Verify },
                };
                if (gzip) h.AutomaticDecompression = DecompressionMethods.GZip;
                return h;
            }
        }

        internal static HttpClient Client(TimeSpan timeout, bool gzip = false)
            => new HttpClient(Handler(gzip), disposeHandler: false) { Timeout = timeout };

        // ---------------------------------------------------------------- downloads that keep going while data arrives (0.15.28)
        /// <summary>How long a download may go without receiving a single byte before it is given up.</summary>
        internal static readonly TimeSpan DownloadIdle = TimeSpan.FromSeconds(45);
        /// <summary>The longest a download may take in all, however slowly it keeps arriving.</summary>
        internal static readonly TimeSpan DownloadMax = TimeSpan.FromMinutes(30);

        /// <summary>The result of Download: the HTTP status, and the body when the status was a success.</summary>
        internal readonly struct Downloaded
        {
            internal readonly HttpStatusCode Status;
            internal readonly byte[] Data;
            internal Downloaded(HttpStatusCode status, byte[] data) { Status = status; Data = data; }
            internal bool Ok => Data != null;
        }

        /// <summary>
        /// Downloads a file for as long as it keeps arriving. A fixed time limit (120 s for the mod update before
        /// 0.15.28) gave up on a player whose connection was slow but working (2026-11-12: the sign-in alone took
        /// 107 s). Here the download is only given up when no data at all arrives for DownloadIdle (the connection
        /// has stalled or dropped), or after DownloadMax in all. progress(received, total) is called from the
        /// download's thread, at most about twice a second; total is -1 when the server does not say the size.
        /// Throws on a stalled or failed download; a non-success status comes back as Status with no Data.
        /// </summary>
        internal static async System.Threading.Tasks.Task<Downloaded> Download(HttpRequestMessage request, bool gzip = false,
            Action<long, long> progress = null)
        {
            using var http = new HttpClient(Handler(gzip), disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
            using var all = new System.Threading.CancellationTokenSource(DownloadMax);
            using var idle = new System.Threading.CancellationTokenSource(DownloadIdle);
            using var both = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(all.Token, idle.Token);
            try
            {
                using var reply = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, both.Token);
                if (!reply.IsSuccessStatusCode) return new Downloaded(reply.StatusCode, null);
                long total = reply.Content.Headers.ContentLength ?? -1;
                using var body = await reply.Content.ReadAsStreamAsync(both.Token);
                using var into = total > 0 && total < int.MaxValue ? new System.IO.MemoryStream((int)total) : new System.IO.MemoryStream();
                var buffer = new byte[64 * 1024];
                long got = 0;
                var lastReport = DateTime.UtcNow;
                while (true)
                {
                    idle.CancelAfter(DownloadIdle);                  // data keeps coming: the idle clock starts again
                    int n = await body.ReadAsync(buffer, 0, buffer.Length, both.Token);
                    if (n == 0) break;
                    into.Write(buffer, 0, n);
                    got += n;
                    if (progress != null && (DateTime.UtcNow - lastReport).TotalMilliseconds >= 500)
                    {
                        lastReport = DateTime.UtcNow;
                        try { progress(got, total); } catch { }
                    }
                }
                if (total > 0 && got != total) throw new System.IO.IOException($"the download ended early ({got} of {total} bytes)");
                try { progress?.Invoke(got, total); } catch { }
                return new Downloaded(reply.StatusCode, into.ToArray());
            }
            catch (OperationCanceledException) when (idle.IsCancellationRequested && !all.IsCancellationRequested)
            {
                throw new TimeoutException($"no data arrived for {DownloadIdle.TotalSeconds:0} seconds (the connection stalled or dropped)");
            }
            catch (OperationCanceledException) when (all.IsCancellationRequested)
            {
                throw new TimeoutException($"the download took longer than {DownloadMax.TotalMinutes:0} minutes");
            }
        }

        /// <summary>Download for a plain GET.</summary>
        internal static System.Threading.Tasks.Task<Downloaded> Download(string url, bool gzip = false, Action<long, long> progress = null)
            => Download(new HttpRequestMessage(HttpMethod.Get, url), gzip, progress);

        // ---------------------------------------------------------------- the mod's own server calls (M-25)
        // Nine places built "POST JSON to /playfab/Revival/..., with the session, read data / errorMessage" by
        // hand, three ways, two with their own clients and timeouts, one with its own retry, and the same message
        // fix-up in three. One way since 0.13.75.

        /// <summary>The server's answer to one of the mod's own calls.</summary>
        internal sealed class Reply
        {
            /// <summary>The call worked and the answer has a "data" part.</summary>
            public bool Ok;
            public int Status;
            /// <summary>The answer's "data" (a copy, usable after the call); default when not Ok.</summary>
            public System.Text.Json.JsonElement Data;
            /// <summary>The server's error code ("error"), when it gave one.</summary>
            public string Code;
            /// <summary>A sentence for the player: the server's errorMessage (capitalised, with a full stop), or
            /// why the server could not be reached.</summary>
            public string Error;
            /// <summary>The call never got an answer (no connection, timed out).</summary>
            public bool Unreachable;
        }

        /// <summary>Writes a JSON object with the given writer calls.</summary>
        internal static string Json(Action<System.Text.Json.Utf8JsonWriter> write)
        {
            using var ms = new System.IO.MemoryStream();
            using (var w = new System.Text.Json.Utf8JsonWriter(ms)) { w.WriteStartObject(); write(w); w.WriteEndObject(); }
            return System.Text.Encoding.UTF8.GetString(ms.ToArray());
        }

        /// <summary>
        /// POSTs <paramref name="json"/> to the revival server at <paramref name="path"/> ("/playfab/Revival/..."),
        /// with the game's session (X-Authorization) when signed in and any <paramref name="headers"/>. A connection
        /// that fails before an answer is tried once more on a new one (the usual cause is a kept-open connection
        /// the server had already closed) when <paramref name="retry"/> is set: only for calls a repeat does no harm
        /// to. Never throws.
        /// </summary>
        internal static async System.Threading.Tasks.Task<Reply> PostJson(string path, string json, TimeSpan timeout,
            System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<string, string>> headers = null, bool session = true,
            bool retry = false)
        {
            var result = new Reply();
            string ticket = session ? PlayFabTransport.SessionTicket : null;
            using var http = Client(timeout);
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var msg = new HttpRequestMessage(HttpMethod.Post, RevivalMod.Config.ServerUrl + path)
                    {
                        Content = new StringContent(json ?? "{}", System.Text.Encoding.UTF8, "application/json")
                    };
                    if (!string.IsNullOrEmpty(ticket)) msg.Headers.TryAddWithoutValidation("X-Authorization", ticket);
                    string lang = Loc.ServerLanguage();                     // the server answers its messages in it
                    if (lang != null) msg.Headers.TryAddWithoutValidation("X-Revival-Lang", lang);
                    if (headers != null) foreach (var h in headers) if (h.Value != null) msg.Headers.TryAddWithoutValidation(h.Key, h.Value);
                    using var reply = await http.SendAsync(msg);
                    result.Status = (int)reply.StatusCode;
                    string text = await reply.Content.ReadAsStringAsync();
                    System.Text.Json.JsonElement root = default;
                    bool parsed = false;
                    try { using var doc = System.Text.Json.JsonDocument.Parse(text); root = doc.RootElement.Clone(); parsed = true; } catch { }
                    if (reply.IsSuccessStatusCode && parsed && root.ValueKind == System.Text.Json.JsonValueKind.Object && root.TryGetProperty("data", out var data))
                    {
                        result.Ok = true;
                        result.Data = data;
                        return result;
                    }
                    if (!parsed || root.ValueKind != System.Text.Json.JsonValueKind.Object)
                    {
                        // not the server's own answer (a proxy's or a network's error page): as the hand-built calls
                        // treated it, the server could not be reached
                        result.Unreachable = true;
                        result.Error = Loc.T("common.unreachableDot", "Could not reach the server.");
                        return result;
                    }
                    {
                        result.Code = root.TryGetProperty("error", out var ec) && ec.ValueKind == System.Text.Json.JsonValueKind.String ? ec.GetString() : null;
                        string why = root.TryGetProperty("errorMessage", out var em) && em.ValueKind == System.Text.Json.JsonValueKind.String ? em.GetString() : null;
                        result.Error = Sentence(why);
                    }
                    result.Error ??= Loc.T("common.notAccepted", "The server did not accept that ({status}).", ("status", result.Status));     // shown to the player
                    return result;
                }
                // tried again only where a repeat does no harm (retry: true); the server does not recognise a repeated
                // /Revival/ call, and a failed connection may have reached it
                catch (Exception e) when (retry && attempt == 1 && e is HttpRequestException)
                {
                    await System.Threading.Tasks.Task.Delay(500);
                }
                catch (Exception e)
                {
                    result.Unreachable = true;
                    result.Error = Loc.T("common.unreachableDot", "Could not reach the server.");
                    RevivalMod.Log.Warning($"[net] {path}: {e.GetType().Name}: {e.Message}");
                    return result;
                }
            }
            result.Unreachable = true;
            result.Error = Loc.T("common.unreachableDot", "Could not reach the server.");
            return result;
        }

        /// <summary>A server message as a sentence: first letter capitalised, ending with a full stop (null for none).</summary>
        internal static string Sentence(string why)
        {
            if (string.IsNullOrWhiteSpace(why)) return null;
            why = why.Trim();
            why = char.ToUpperInvariant(why[0]) + why.Substring(1);
            return why.EndsWith(".") || why.EndsWith("!") || why.EndsWith("?") ? why : why + ".";
        }

        private static X509Certificate2Collection roots;
        private static volatile bool told;

        private static X509Certificate2Collection Roots()
        {
            if (roots != null) return roots;
            var list = new X509Certificate2Collection();
            foreach (string pem in Pems)
            {
                try { list.Add(X509Certificate2.CreateFromPem(pem)); }
                catch (Exception e) { RevivalMod.Log.Warning("[secure] a built-in root certificate could not be read: " + e.Message); }
            }
            return roots = list;
        }

        /// <summary>The same check, for an encrypted connection the mod opens itself (the match tunnel).</summary>
        internal static bool Verify(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors errors)
            => Trusted(cert == null ? null : cert as X509Certificate2 ?? new X509Certificate2(cert), chain, errors);

        private static bool Trusted(X509Certificate2 cert, X509Chain chain, SslPolicyErrors errors)
        {
            if (errors == SslPolicyErrors.None) return true;
            // only a chain problem may be looked at again; a wrong name or a missing certificate never
            if (errors != SslPolicyErrors.RemoteCertificateChainErrors || cert == null) return false;
            try
            {
                using var again = new X509Chain();
                again.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                again.ChainPolicy.CustomTrustStore.AddRange(Roots());
                again.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                if (chain != null)
                    foreach (var element in chain.ChainElements)
                        if (element.Certificate != null) again.ChainPolicy.ExtraStore.Add(element.Certificate);
                bool ok = again.Build(cert);
                if (!told)
                {
                    told = true;
                    string why = "";
                    if (!ok)
                        foreach (var s in again.ChainStatus) why += " " + s.Status;
                    RevivalMod.Log.Msg(ok ? "[secure] the server's certificate was checked against the mod's own list of root authorities"
                                          : "[secure] the server's certificate is not trusted:" + why);
                }
                return ok;
            }
            catch (Exception e)
            {
                RevivalMod.Log.Warning("[secure] could not check the server's certificate: " + e.Message);
                return false;
            }
        }

        private static readonly string[] Pems =
        {
            // ISRG Root X1
            @"-----BEGIN CERTIFICATE-----
MIIFazCCA1OgAwIBAgIRAIIQz7DSQONZRGPgu2OCiwAwDQYJKoZIhvcNAQELBQAw
TzELMAkGA1UEBhMCVVMxKTAnBgNVBAoTIEludGVybmV0IFNlY3VyaXR5IFJlc2Vh
cmNoIEdyb3VwMRUwEwYDVQQDEwxJU1JHIFJvb3QgWDEwHhcNMTUwNjA0MTEwNDM4
WhcNMzUwNjA0MTEwNDM4WjBPMQswCQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJu
ZXQgU2VjdXJpdHkgUmVzZWFyY2ggR3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBY
MTCCAiIwDQYJKoZIhvcNAQEBBQADggIPADCCAgoCggIBAK3oJHP0FDfzm54rVygc
h77ct984kIxuPOZXoHj3dcKi/vVqbvYATyjb3miGbESTtrFj/RQSa78f0uoxmyF+
0TM8ukj13Xnfs7j/EvEhmkvBioZxaUpmZmyPfjxwv60pIgbz5MDmgK7iS4+3mX6U
A5/TR5d8mUgjU+g4rk8Kb4Mu0UlXjIB0ttov0DiNewNwIRt18jA8+o+u3dpjq+sW
T8KOEUt+zwvo/7V3LvSye0rgTBIlDHCNAymg4VMk7BPZ7hm/ELNKjD+Jo2FR3qyH
B5T0Y3HsLuJvW5iB4YlcNHlsdu87kGJ55tukmi8mxdAQ4Q7e2RCOFvu396j3x+UC
B5iPNgiV5+I3lg02dZ77DnKxHZu8A/lJBdiB3QW0KtZB6awBdpUKD9jf1b0SHzUv
KBds0pjBqAlkd25HN7rOrFleaJ1/ctaJxQZBKT5ZPt0m9STJEadao0xAH0ahmbWn
OlFuhjuefXKnEgV4We0+UXgVCwOPjdAvBbI+e0ocS3MFEvzG6uBQE3xDk3SzynTn
jh8BCNAw1FtxNrQHusEwMFxIt4I7mKZ9YIqioymCzLq9gwQbooMDQaHWBfEbwrbw
qHyGO0aoSCqI3Haadr8faqU9GY/rOPNk3sgrDQoo//fb4hVC1CLQJ13hef4Y53CI
rU7m2Ys6xt0nUW7/vGT1M0NPAgMBAAGjQjBAMA4GA1UdDwEB/wQEAwIBBjAPBgNV
HRMBAf8EBTADAQH/MB0GA1UdDgQWBBR5tFnme7bl5AFzgAiIyBpY9umbbjANBgkq
hkiG9w0BAQsFAAOCAgEAVR9YqbyyqFDQDLHYGmkgJykIrGF1XIpu+ILlaS/V9lZL
ubhzEFnTIZd+50xx+7LSYK05qAvqFyFWhfFQDlnrzuBZ6brJFe+GnY+EgPbk6ZGQ
3BebYhtF8GaV0nxvwuo77x/Py9auJ/GpsMiu/X1+mvoiBOv/2X/qkSsisRcOj/KK
NFtY2PwByVS5uCbMiogziUwthDyC3+6WVwW6LLv3xLfHTjuCvjHIInNzktHCgKQ5
ORAzI4JMPJ+GslWYHb4phowim57iaztXOoJwTdwJx4nLCgdNbOhdjsnvzqvHu7Ur
TkXWStAmzOVyyghqpZXjFaH3pO3JLF+l+/+sKAIuvtd7u+Nxe5AW0wdeRlN8NwdC
jNPElpzVmbUq4JUagEiuTDkHzsxHpFKVK7q4+63SM1N95R1NbdWhscdCb+ZAJzVc
oyi3B43njTOQ5yOf+1CceWxG1bQVs5ZufpsMljq4Ui0/1lvh+wjChP4kqKOJ2qxq
4RgqsahDYVvTH9w7jXbyLeiNdd8XM2w9U/t7y0Ff/9yi0GE44Za4rF2LN9d11TPA
mRGunUHBcnWEvgJBQl9nJEiU0Zsnvgc/ubhPgXRR4Xq37Z0j4r7g1SgEEzwxA57d
emyPxgcYxn/eR44/KJ4EBs+lVDR3veyJm+kXQ99b21/+jh5Xos1AnX5iItreGCc=
-----END CERTIFICATE-----",
            // ISRG Root X2
            @"-----BEGIN CERTIFICATE-----
MIICGzCCAaGgAwIBAgIQQdKd0XLq7qeAwSxs6S+HUjAKBggqhkjOPQQDAzBPMQsw
CQYDVQQGEwJVUzEpMCcGA1UEChMgSW50ZXJuZXQgU2VjdXJpdHkgUmVzZWFyY2gg
R3JvdXAxFTATBgNVBAMTDElTUkcgUm9vdCBYMjAeFw0yMDA5MDQwMDAwMDBaFw00
MDA5MTcxNjAwMDBaME8xCzAJBgNVBAYTAlVTMSkwJwYDVQQKEyBJbnRlcm5ldCBT
ZWN1cml0eSBSZXNlYXJjaCBHcm91cDEVMBMGA1UEAxMMSVNSRyBSb290IFgyMHYw
EAYHKoZIzj0CAQYFK4EEACIDYgAEzZvVn4CDCuwJSvMWSj5cz3es3mcFDR0HttwW
+1qLFNvicWDEukWVEYmO6gbf9yoWHKS5xcUy4APgHoIYOIvXRdgKam7mAHf7AlF9
ItgKbppbd9/w+kHsOdx1ymgHDB/qo0IwQDAOBgNVHQ8BAf8EBAMCAQYwDwYDVR0T
AQH/BAUwAwEB/zAdBgNVHQ4EFgQUfEKWrt5LSDv6kviejM9ti6lyN5UwCgYIKoZI
zj0EAwMDaAAwZQIwe3lORlCEwkSHRhtFcP9Ymd70/aTSVaYgLXTWNLxBo1BfASdW
tL4ndQavEi51mI38AjEAi/V3bNTIZargCyzuFJ0nN6T5U6VR5CmD1/iQMVtCnwr1
/q4AaOeMSQ+2b1tbFfLn
-----END CERTIFICATE-----",
            // USERTrust RSA Certification Authority
            @"-----BEGIN CERTIFICATE-----
MIIF3jCCA8agAwIBAgIQAf1tMPyjylGoG7xkDjUDLTANBgkqhkiG9w0BAQwFADCB
iDELMAkGA1UEBhMCVVMxEzARBgNVBAgTCk5ldyBKZXJzZXkxFDASBgNVBAcTC0pl
cnNleSBDaXR5MR4wHAYDVQQKExVUaGUgVVNFUlRSVVNUIE5ldHdvcmsxLjAsBgNV
BAMTJVVTRVJUcnVzdCBSU0EgQ2VydGlmaWNhdGlvbiBBdXRob3JpdHkwHhcNMTAw
MjAxMDAwMDAwWhcNMzgwMTE4MjM1OTU5WjCBiDELMAkGA1UEBhMCVVMxEzARBgNV
BAgTCk5ldyBKZXJzZXkxFDASBgNVBAcTC0plcnNleSBDaXR5MR4wHAYDVQQKExVU
aGUgVVNFUlRSVVNUIE5ldHdvcmsxLjAsBgNVBAMTJVVTRVJUcnVzdCBSU0EgQ2Vy
dGlmaWNhdGlvbiBBdXRob3JpdHkwggIiMA0GCSqGSIb3DQEBAQUAA4ICDwAwggIK
AoICAQCAEmUXNg7D2wiz0KxXDXbtzSfTTK1Qg2HiqiBNCS1kCdzOiZ/MPans9s/B
3PHTsdZ7NygRK0faOca8Ohm0X6a9fZ2jY0K2dvKpOyuR+OJv0OwWIJAJPuLodMkY
tJHUYmTbf6MG8YgYapAiPLz+E/CHFHv25B+O1ORRxhFnRghRy4YUVD+8M/5+bJz/
Fp0YvVGONaanZshyZ9shZrHUm3gDwFA66Mzw3LyeTP6vBZY1H1dat//O+T23LLb2
VN3I5xI6Ta5MirdcmrS3ID3KfyI0rn47aGYBROcBTkZTmzNg95S+UzeQc0PzMsNT
79uq/nROacdrjGCT3sTHDN/hMq7MkztReJVni+49Vv4M0GkPGw/zJSZrM233bkf6
c0Plfg6lZrEpfDKEY1WJxA3Bk1QwGROs0303p+tdOmw1XNtB1xLaqUkL39iAigmT
Yo61Zs8liM2EuLE/pDkP2QKe6xJMlXzzawWpXhaDzLhn4ugTncxbgtNMs+1b/97l
c6wjOy0AvzVVdAlJ2ElYGn+SNuZRkg7zJn0cTRe8yexDJtC/QV9AqURE9JnnV4ee
UB9XVKg+/XRjL7FQZQnmWEIuQxpMtPAlR1n6BB6T1CZGSlCBst6+eLf8ZxXhyVeE
Hg9j1uliutZfVS7qXMYoCAQlObgOK6nyTJccBz8NUvXt7y+CDwIDAQABo0IwQDAd
BgNVHQ4EFgQUU3m/WqorSs9UgOHYm8Cd8rIDZsswDgYDVR0PAQH/BAQDAgEGMA8G
A1UdEwEB/wQFMAMBAf8wDQYJKoZIhvcNAQEMBQADggIBAFzUfA3P9wF9QZllDHPF
Up/L+M+ZBn8b2kMVn54CVVeWFPFSPCeHlCjtHzoBN6J2/FNQwISbxmtOuowhT6KO
VWKR82kV2LyI48SqC/3vqOlLVSoGIG1VeCkZ7l8wXEskEVX/JJpuXior7gtNn3/3
ATiUFJVDBwn7YKnuHKsSjKCaXqeYalltiz8I+8jRRa8YFWSQEg9zKC7F4iRO/Fjs
8PRF/iKz6y+O0tlFYQXBl2+odnKPi4w2r78NBc5xjeambx9spnFixdjQg3IM8WcR
iQycE0xyNN+81XHfqnHd4blsjDwSXWXavVcStkNr/+XeTWYRUc+ZruwXtuhxkYze
Sf7dNXGiFSeUHM9h4ya7b6NnJSFd5t0dCy5oGzuCr+yDZ4XUmFF0sbmZgIn/f3gZ
XHlKYC6SQK5MNyosycdiyA5d9zZbyuAlJQG03RoHnHcAP9Dc1ew91Pq7P8yF1m9/
qS3fuQL39ZeatTXaw2ewh0qpKJ4jjv9cJ2vhsE/zB+4ALtRZh8tSQZXq9EfX7mRB
VXyNWQKV3WKdwrnuWih0hKWbt5DHDAff9Yk2dDLWKMGwsAvgnEzDHNb842m1R0aB
L6KCq9NjRHDEjf8tM7qtj3u1cIiuPhnPQCjY/MiQu12ZIvVS5ljFH4gxQ+6IHdfG
jjxDah2nGN59PRbxYvnKkKj9
-----END CERTIFICATE-----",
            // USERTrust ECC Certification Authority
            @"-----BEGIN CERTIFICATE-----
MIICjzCCAhWgAwIBAgIQXIuZxVqUxdJxVt7NiYDMJjAKBggqhkjOPQQDAzCBiDEL
MAkGA1UEBhMCVVMxEzARBgNVBAgTCk5ldyBKZXJzZXkxFDASBgNVBAcTC0plcnNl
eSBDaXR5MR4wHAYDVQQKExVUaGUgVVNFUlRSVVNUIE5ldHdvcmsxLjAsBgNVBAMT
JVVTRVJUcnVzdCBFQ0MgQ2VydGlmaWNhdGlvbiBBdXRob3JpdHkwHhcNMTAwMjAx
MDAwMDAwWhcNMzgwMTE4MjM1OTU5WjCBiDELMAkGA1UEBhMCVVMxEzARBgNVBAgT
Ck5ldyBKZXJzZXkxFDASBgNVBAcTC0plcnNleSBDaXR5MR4wHAYDVQQKExVUaGUg
VVNFUlRSVVNUIE5ldHdvcmsxLjAsBgNVBAMTJVVTRVJUcnVzdCBFQ0MgQ2VydGlm
aWNhdGlvbiBBdXRob3JpdHkwdjAQBgcqhkjOPQIBBgUrgQQAIgNiAAQarFRaqflo
I+d61SRvU8Za2EurxtW20eZzca7dnNYMYf3boIkDuAUU7FfO7l0/4iGzzvfUinng
o4N+LZfQYcTxmdwlkWOrfzCjtHDix6EznPO/LlxTsV+zfTJ/ijTjeXmjQjBAMB0G
A1UdDgQWBBQ64QmG1M8ZwpZ2dEl23OA1xmNjmjAOBgNVHQ8BAf8EBAMCAQYwDwYD
VR0TAQH/BAUwAwEB/zAKBggqhkjOPQQDAwNoADBlAjA2Z6EWCNzklwBBHU6+4WMB
zzuqQhFkoJ2UOQIReVx7Hfpkue4WQrO/isIJxOzksU0CMQDpKmFHjFJKS04YcPbW
RNZu9YO6bVi9JNlWSOrvxKJGgYhqOkbRqZtNyWHa0V1Xahg=
-----END CERTIFICATE-----",
            // GTS Root R1
            @"-----BEGIN CERTIFICATE-----
MIIFVzCCAz+gAwIBAgINAgPlk28xsBNJiGuiFzANBgkqhkiG9w0BAQwFADBHMQsw
CQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEU
MBIGA1UEAxMLR1RTIFJvb3QgUjEwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAw
MDAwWjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZp
Y2VzIExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjEwggIiMA0GCSqGSIb3DQEBAQUA
A4ICDwAwggIKAoICAQC2EQKLHuOhd5s73L+UPreVp0A8of2C+X0yBoJx9vaMf/vo
27xqLpeXo4xL+Sv2sfnOhB2x+cWX3u+58qPpvBKJXqeqUqv4IyfLpLGcY9vXmX7w
Cl7raKb0xlpHDU0QM+NOsROjyBhsS+z8CZDfnWQpJSMHobTSPS5g4M/SCYe7zUjw
TcLCeoiKu7rPWRnWr4+wB7CeMfGCwcDfLqZtbBkOtdh+JhpFAz2weaSUKK0Pfybl
qAj+lug8aJRT7oM6iCsVlgmy4HqMLnXWnOunVmSPlk9orj2XwoSPwLxAwAtcvfaH
szVsrBhQf4TgTM2S0yDpM7xSma8ytSmzJSq0SPly4cpk9+aCEI3oncKKiPo4Zor8
Y/kB+Xj9e1x3+naH+uzfsQ55lVe0vSbv1gHR6xYKu44LtcXFilWr06zqkUspzBmk
MiVOKvFlRNACzqrOSbTqn3yDsEB750Orp2yjj32JgfpMpf/VjsPOS+C12LOORc92
wO1AK/1TD7Cn1TsNsYqiA94xrcx36m97PtbfkSIS5r762DL8EGMUUXLeXdYWk70p
aDPvOmbsB4om3xPXV2V4J95eSRQAogB/mqghtqmxlbCluQ0WEdrHbEg8QOB+DVrN
VjzRlwW5y0vtOUucxD/SVRNuJLDWcfr0wbrM7Rv1/oFB2ACYPTrIrnqYNxgFlQID
AQABo0IwQDAOBgNVHQ8BAf8EBAMCAYYwDwYDVR0TAQH/BAUwAwEB/zAdBgNVHQ4E
FgQU5K8rJnEaK0gnhS9SZizv8IkTcT4wDQYJKoZIhvcNAQEMBQADggIBAJ+qQibb
C5u+/x6Wki4+omVKapi6Ist9wTrYggoGxval3sBOh2Z5ofmmWJyq+bXmYOfg6LEe
QkEzCzc9zolwFcq1JKjPa7XSQCGYzyI0zzvFIoTgxQ6KfF2I5DUkzps+GlQebtuy
h6f88/qBVRRiClmpIgUxPoLW7ttXNLwzldMXG+gnoot7TiYaelpkttGsN/H9oPM4
7HLwEXWdyzRSjeZ2axfG34arJ45JK3VmgRAhpuo+9K4l/3wV3s6MJT/KYnAK9y8J
ZgfIPxz88NtFMN9iiMG1D53Dn0reWVlHxYciNuaCp+0KueIHoI17eko8cdLiA6Ef
MgfdG+RCzgwARWGAtQsgWSl4vflVy2PFPEz0tv/bal8xa5meLMFrUKTX5hgUvYU/
Z6tGn6D/Qqc6f1zLXbBwHSs09dR2CQzreExZBfMzQsNhFRAbd03OIozUhfJFfbdT
6u9AWpQKXCBfTkBdYiJ23//OYb2MI3jSNwLgjt7RETeJ9r/tSQdirpLsQBqvFAnZ
0E6yove+7u7Y/9waLd64NnHi/Hm3lCXRSHNboTXns5lndcEZOitHTtNCjv0xyBZm
2tIMPNuzjsmhDYAPexZ3FL//2wmUspO8IFgV6dtxQ/PeEMMA3KgqlbbC1j+Qa3bb
bP6MvPJwNQzcmRk13NfIRmPVNnGuV/u3gm3c
-----END CERTIFICATE-----",
            // GTS Root R4
            @"-----BEGIN CERTIFICATE-----
MIICCTCCAY6gAwIBAgINAgPlwGjvYxqccpBQUjAKBggqhkjOPQQDAzBHMQswCQYD
VQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2VzIExMQzEUMBIG
A1UEAxMLR1RTIFJvb3QgUjQwHhcNMTYwNjIyMDAwMDAwWhcNMzYwNjIyMDAwMDAw
WjBHMQswCQYDVQQGEwJVUzEiMCAGA1UEChMZR29vZ2xlIFRydXN0IFNlcnZpY2Vz
IExMQzEUMBIGA1UEAxMLR1RTIFJvb3QgUjQwdjAQBgcqhkjOPQIBBgUrgQQAIgNi
AATzdHOnaItgrkO4NcWBMHtLSZ37wWHO5t5GvWvVYRg1rkDdc/eJkTBa6zzuhXyi
QHY7qca4R9gq55KRanPpsXI5nymfopjTX15YhmUPoYRlBtHci8nHc8iMai/lxKvR
HYqjQjBAMA4GA1UdDwEB/wQEAwIBhjAPBgNVHRMBAf8EBTADAQH/MB0GA1UdDgQW
BBSATNbrdP9JNqPV2Py1PsVq8JQdjDAKBggqhkjOPQQDAwNpADBmAjEA6ED/g94D
9J+uHXqnLrmvT/aDHQ4thQEd0dlq7A/Cr8deVl5c1RxYIigL9zC2L7F8AjEA8GE8
p/SgguMh1YQdc4acLa/KNJvxn7kjNuK8YAOdgLOaVsjh4rsUecrNIdSUtUlD
-----END CERTIFICATE-----",
        };
    }
}
