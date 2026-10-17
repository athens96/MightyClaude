using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

// The phone's direct path to a Windows host (docs/relay.md, "Tailscale 직접 연결"; spec
// docs/superpowers/specs/2026-10-17-windows-catch-up-design.md section 2). Loopback stands in for Tailscale only through
// the host's internal SetDirectAddressSource seam; nothing here runs netsh or binds a real Tailscale address.
internal static class MobileDirectVerification
{
    private static void Check(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }

    internal static Task EndpointsUpgradeAdmissionAndStatus()
    {
        // Endpoints: host:port, IPv6 in brackets; only Tailscale addresses, each once, at most eight.
        Check(MobileDirect.Endpoint("100.101.102.103", 43139) == "100.101.102.103:43139" && MobileDirect.Endpoint("fd7a:115c:a1e0::1", 43139) == "[fd7a:115c:a1e0::1]:43139", "endpoints are host:port, IPv6 in brackets");
        Check(MobileDirect.Advertised([("100.101.102.103", 43139), ("fd7a:115c:a1e0::1", 43139), ("192.168.0.5", 43139), ("100.101.102.103", 43139)])
            .SequenceEqual(["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139"]), "only Tailscale endpoints, each once");
        Check(MobileDirect.Advertised([("127.0.0.1", 50000)], allowAnyHost: true).SequenceEqual(["127.0.0.1:50000"]) && MobileDirect.Advertised([("127.0.0.1", 50000)]).Count == 0, "loopback is advertised only through the seam");
        Check(MobileDirect.Advertised(Enumerable.Range(1, 12).Select(i => ("100.101.0." + i.ToString(CultureInfo.InvariantCulture), 43139))).Count == MobileDirect.MaximumEndpoints, "at most eight endpoints");
        Check(MobileDirect.ValidPort(1024) && MobileDirect.ValidPort(65535) && !MobileDirect.ValidPort(1023) && !MobileDirect.ValidPort(65536) && !MobileDirect.ValidPort(0), "ports are 1024-65535");
        Check(MobileDirect.IsDirect("direct-0f8fad5b-d9cb-469f-a165-70867728950e") && !MobileDirect.IsDirect("0f8fad5b-d9cb-469f-a165-70867728950e"), "direct ids start with direct-");
        Check(MobileDirect.DefaultPort == 43139 && MobileDirect.ConnectionLimit == 36 && MobileDirect.MaximumFrameBytes == 1024 * 1024 && MobileDirect.HandshakeDeadline == TimeSpan.FromSeconds(10)
            && MobileDirect.AuthFailureWindow == TimeSpan.FromSeconds(60) && MobileDirect.RedetectInterval == TimeSpan.FromSeconds(15) && MobileDirect.GenericRefusal == "unauthorized", "the Mac's limits");

        // The phone's upgrade (RFC 6455 section 1.3's sample key): GET /ws, HTTP/1.1, websocket, version 13, a 16-byte key.
        Check(MobileDirect.AcceptKey("dGhlIHNhbXBsZSBub25jZQ==") == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=", "the accept key");
        const string request = "GET /ws?v=1 HTTP/1.1\r\nHost: 100.101.102.103:43139\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\n\r\n";
        Check(MobileDirect.UpgradeResponse(request) == "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: s3pPLMBiTxaQ9kYGzzhZRbK+xOo=\r\n\r\n", "a phone's upgrade is accepted");
        Check(MobileDirect.UpgradeResponse(request.Replace("Connection: Upgrade", "Connection: keep-alive, Upgrade").Replace("Upgrade: websocket", "upgrade: WebSocket")) is not null, "header names and tokens are case-insensitive");
        foreach (var bad in new[]
                 {
                     request.Replace("GET /ws?v=1", "GET /other"), request.Replace("GET", "POST"), request.Replace("HTTP/1.1", "HTTP/1.0"),
                     request.Replace("Sec-WebSocket-Version: 13", "Sec-WebSocket-Version: 8"), request.Replace("Upgrade: websocket\r\n", ""),
                     request.Replace("dGhlIHNhbXBsZSBub25jZQ==", "c2hvcnQ="), request.Replace("Connection: Upgrade", "Connection: close"),
                 })
            Check(MobileDirect.UpgradeResponse(bad) is null, "a refused upgrade: " + bad.Split("\r\n")[0]);

        // Admission at accept, before anything is read.
        const string tailnet = "100.101.102.103";
        Check(MobileDirect.Admit(tailnet, false, 0, [], 0), "a tailnet peer gets a slot");
        Check(!MobileDirect.Admit("192.168.0.5", false, 0, [], 0) && !MobileDirect.Admit("127.0.0.1", false, 0, [], 0) && !MobileDirect.Admit(null, false, 0, [], 0), "a peer outside the ranges is dropped");
        Check(MobileDirect.Admit("127.0.0.1", true, 0, [], 0), "loopback only through the seam");
        Check(!MobileDirect.Admit(tailnet, false, MobileDirect.MaximumClients, [], 0), "a full phone table admits none");
        Check(!MobileDirect.Admit(tailnet, false, 4, ["100.64.0.1", "100.64.0.2", "100.64.0.3", "100.64.0.4"], 0), "four handshakes at once");
        Check(!MobileDirect.Admit(tailnet, false, 2, [tailnet, tailnet], 0) && MobileDirect.Admit(tailnet, false, 2, [tailnet, "100.64.0.1"], 0), "two handshakes per address");
        Check(!MobileDirect.Admit(tailnet, false, 0, [], MobileDirect.MaximumAuthFailures) && MobileDirect.Admit(tailnet, false, 0, [], MobileDirect.MaximumAuthFailures - 1), "five failed auths shut the address out");

        // Failed auths per address inside 60 s.
        var failures = new DirectAuthFailures(); var start = DateTimeOffset.UnixEpoch;
        for (var i = 0; i < 5; i++) failures.Record(tailnet, start.AddSeconds(i));
        Check(failures.Recent(tailnet, start.AddSeconds(10)) == 5 && failures.Recent("100.64.0.1", start) == 0, "failures count per address");
        Check(failures.Recent(tailnet, start.AddSeconds(62)) == 2, "a failure 60 s old no longer counts");
        Check(failures.Recent(tailnet, start.AddSeconds(70)) == 0 && failures.Peers == 0, "an address with no recent failure is forgotten");
        for (var i = 0; i < 256; i++) failures.Record("100.65." + (i / 256).ToString(CultureInfo.InvariantCulture) + "." + (i % 256).ToString(CultureInfo.InvariantCulture), start);
        failures.Record("100.66.0.1", start.AddSeconds(61));
        Check(failures.Peers == 1, "a full table drops the addresses whose failures are all old");

        // The status line says what the Mac's says.
        Check(MobileDirectStatusText.Detail(MobileDirectStatus.Off) == "", "off says nothing");
        Check(MobileDirectStatusText.Detail(new("disabled", [])) == Locale.Get("mobileRemote.direct.status.off"), "the switch off");
        Check(MobileDirectStatusText.Detail(new("notFound", [])) == Locale.Get("mobileRemote.direct.status.notFound"), "no Tailscale");
        Check(MobileDirectStatusText.Detail(new("listening", ["100.101.102.103:43139", "[fd7a:115c:a1e0::1]:43139"]))
            == Locale.Get("mobileRemote.direct.status.listeningTemplate", new Dictionary<string, string> { ["addresses"] = "100.101.102.103:43139, [fd7a:115c:a1e0::1]:43139" }), "the endpoints listened on");
        Check(MobileDirectStatusText.Detail(new("failed", [], "in use")) == Locale.Get("mobileRemote.direct.status.failedTemplate", new Dictionary<string, string> { ["error"] = "in use" }), "why binding failed");
        Check(new MobileDirectStatus("listening", ["x"]).Listening && !MobileDirectStatus.Off.Listening, "Listening is computed");
        Check(JsonSerializer.Serialize(MobileDirectStatus.Off, Wire.Json) == "{\"state\":\"off\",\"endpoints\":[]}", "the computed property is never serialized");
        return Task.CompletedTask;
    }
}
