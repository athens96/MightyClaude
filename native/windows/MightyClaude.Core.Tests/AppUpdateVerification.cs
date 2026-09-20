using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using MightyClaude.Core;

// Checks for the Windows app self-update. Every name registered in
// Verification.RunAsync starts with "app update".
//
// Nothing here opens a socket: the service is handed an HttpMessageHandler that
// answers from memory. Nothing here starts a process: the replacement is handed
// a wait-for-exit and a start-app callback. The key pair is generated for the
// tests only and never leaves this file.
internal static class AppUpdateVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static byte[] Hex(string value) => Convert.FromHexString(value);
    private static string Temp() { var path = Path.Combine(Path.GetTempPath(), "mighty-appupdate-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }

    private static async Task<string> Refused(Func<Task> action, string message)
    {
        try { await action(); }
        catch (Exception error) { return error.Message; }
        throw new InvalidOperationException(message);
    }

    // The fixture key pair: generated for these checks only.
    private static readonly byte[] FixtureSeed = Enumerable.Range(0, 32).Select(i => (byte)(i * 7 + 3)).ToArray();
    private static readonly byte[] FixtureKey = Ed25519Fixture.PublicKey(FixtureSeed);
    private static readonly byte[] OtherKey = Ed25519Fixture.PublicKey(Enumerable.Range(0, 32).Select(i => (byte)(i + 1)).ToArray());

    // RFC 8032 section 7.1 vectors: seed, public key, message, signature.
    private static readonly (string Seed, string Key, string Message, string Signature)[] Rfc8032 =
    [
        ("9d61b19deffd5a60ba844af492ec2cc44449c5697b326919703bac031cae7f60",
         "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
         "",
         "e5564300c360ac729086e2cc806e828a84877f1eb8e5d974d873e065224901555fb8821590a33bacc61e39701cf9b46bd25bf5f0595bbe24655141438e7a100b"),
        ("4ccd089b28ff96da9db6c346ec114e0f5b8a319f35aba624da8cf6ed4fb8a6fb",
         "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c",
         "72",
         "92a009a9f0d4cab8720e820b5f642540a2b27b5416503f8fb3762223ebdb69da085ac1e43e15996e458f3613d0f11d8c387b2eaeb4302aeeb00d291612bb0c00"),
        ("c5aa8df43f9f837bedb7442f31dcb7b166d38535076f094b85ce3a2e0b4458f7",
         "fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025",
         "af82",
         "6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a"),
        ("833fe62409237b9d62ec77587520911e9a759cec1d19755b7da901b96dca3d42",
         "ec172b93ad5e563bf4932c70e1245034c35467ef2efd4d64ebf819683467e2bf",
         "ddaf35a193617abacc417349ae20413112e6fa4e89a97ea20a9eeee64b55d39a2192992a274fc1a836ba3c23a3feebbd454d4423643ce80e2a9ac94fa54ca49f",
         "dc2a4459e7369633a52b1bf277839a00201009a3efbf3ecb69bea2186c26b58909351fc9ac90b3ecfdfbc7c66431e0303dca179c138ac17ad9bef1177331a704"),
    ];

    /// The verify-only Ed25519 the client carries answers the RFC 8032 vectors,
    /// refuses a flipped bit anywhere, and agrees with the fixture signer the
    /// rest of these checks use.
    internal static Task Ed25519MatchesTheRfc8032Vectors()
    {
        foreach (var (seed, key, message, signature) in Rfc8032)
        {
            var publicKey = Hex(key);
            var bytes = Hex(message);
            var sig = Hex(signature);
            Check(Ed25519Verify.Verify(bytes, sig, publicKey), "RFC 8032 vector " + key[..8] + " did not verify");

            // The fixture signer must reproduce the vector, or a manifest signed
            // with it would prove nothing.
            Check(Ed25519Fixture.PublicKey(Hex(seed)).SequenceEqual(publicKey), "fixture signer derived another public key");
            Check(Ed25519Fixture.Sign(Hex(seed), bytes).SequenceEqual(sig), "fixture signer produced another signature");

            var changedMessage = bytes.Length == 0 ? [(byte)1] : bytes.ToArray();
            if (bytes.Length > 0) changedMessage[0] ^= 0x01;
            Check(!Ed25519Verify.Verify(changedMessage, sig, publicKey), "a changed message must not verify");

            var changedSignature = sig.ToArray(); changedSignature[0] ^= 0x01;
            Check(!Ed25519Verify.Verify(bytes, changedSignature, publicKey), "a changed signature must not verify");

            var changedKey = publicKey.ToArray(); changedKey[0] ^= 0x01;
            Check(!Ed25519Verify.Verify(bytes, sig, changedKey), "another public key must not verify");

            Check(!Ed25519Verify.Verify(bytes, sig[..63], publicKey), "a short signature must not verify");
            Check(!Ed25519Verify.Verify(bytes, sig, publicKey[..31]), "a short public key must not verify");
        }
        return Task.CompletedTask;
    }

    // — Manifest fixtures —

    private static string PlainManifest(
        string version = "9.9.9",
        string? x64Sha = null, long? x64Size = 4096,
        string? arm64Sha = null, long? arm64Size = 4096,
        string scheme = "https")
    {
        string Asset(string arch, string? sha, long? size)
        {
            var parts = new List<string> { $"\"url\":\"{scheme}://updates.example/{version}/MightyClaude-windows-{arch}.zip\"" };
            if (sha is not null) parts.Add($"\"sha256\":\"{sha}\"");
            if (size is not null) parts.Add($"\"size\":{size}");
            return "{" + string.Join(",", parts) + "}";
        }
        return "{\"version\":\"" + version + "\",\"notes\":\"픽스처 릴리스 노트\",\"windows\":{"
            + "\"x64\":" + Asset("x64", x64Sha ?? new string('a', 64), x64Size) + ","
            + "\"arm64\":" + Asset("arm64", arm64Sha ?? new string('b', 64), arm64Size)
            + "}}";
    }

    private static byte[] Envelope(string plain, byte[]? seed = null, string format = AppUpdateManifest.EnvelopeFormat)
    {
        var payload = Encoding.UTF8.GetBytes(plain);
        var signature = Ed25519Fixture.Sign(seed ?? FixtureSeed, payload);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            format,
            version = "1",
            payload = Convert.ToBase64String(payload),
            signature = Convert.ToBase64String(signature),
        });
    }

    /// A signed envelope is accepted; an unsigned manifest, a tampered payload,
    /// a signature from another key and an unknown envelope format are refused.
    internal static Task ManifestAcceptsAFixtureSignatureAndRefusesEverythingElse()
    {
        var plain = PlainManifest();
        var manifest = AppUpdateManifest.Parse(Envelope(plain), FixtureKey);
        Check(manifest.Version == "9.9.9", "the signed manifest version must be read");
        Check(manifest.Windows.Count == 2, "both Windows architectures must be read");
        Check(manifest.Notes == "픽스처 릴리스 노트", "the release notes must survive the envelope");

        void Refuse(byte[] data, byte[] key, string what)
        {
            try { AppUpdateManifest.Parse(data, key); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException(what + " must be refused");
        }

        Refuse(Encoding.UTF8.GetBytes(plain), FixtureKey, "an unsigned manifest");
        Refuse(Envelope(plain), OtherKey, "a signature from another key");
        Refuse(Envelope(plain, format: "mightyclaude-update-v2"), FixtureKey, "an unknown envelope format");

        // A tampered payload: the version is raised without re-signing.
        var tampered = Envelope(plain);
        var document = JsonDocument.Parse(tampered).RootElement;
        var forged = JsonSerializer.SerializeToUtf8Bytes(new
        {
            format = AppUpdateManifest.EnvelopeFormat,
            payload = Convert.ToBase64String(Encoding.UTF8.GetBytes(PlainManifest("99.0.0"))),
            signature = document.GetProperty("signature").GetString(),
        });
        Refuse(forged, FixtureKey, "a tampered payload");
        Refuse("{\"format\":\"mightyclaude-update-v1\",\"payload\":\"not base64!!\",\"signature\":\"AA==\"}"u8.ToArray(),
            FixtureKey, "a payload that is not base64");
        return Task.CompletedTask;
    }

    /// Windows rule 2: an asset without sha256 or without size is refused, and
    /// so is an asset whose url is not https.
    internal static Task ManifestRefusesAnAssetWithoutSha256OrSize()
    {
        var both = AppUpdateManifest.Parse(Envelope(PlainManifest()), FixtureKey);
        Check(both.Windows.ContainsKey("x64") && both.Windows.ContainsKey("arm64"), "a complete asset must be kept");

        var noSha = AppUpdateManifest.Parse(Envelope(PlainManifest(x64Sha: "")), FixtureKey);
        Check(!noSha.Windows.ContainsKey("x64"), "an asset without sha256 must be refused");
        Check(noSha.Windows.ContainsKey("arm64"), "the other architecture must be unaffected");

        var noSize = AppUpdateManifest.Parse(Envelope(PlainManifest(arm64Size: null)), FixtureKey);
        Check(!noSize.Windows.ContainsKey("arm64"), "an asset without size must be refused");

        var zeroSize = AppUpdateManifest.Parse(Envelope(PlainManifest(arm64Size: 0)), FixtureKey);
        Check(!zeroSize.Windows.ContainsKey("arm64"), "an asset with size 0 must be refused");

        var plainHttp = AppUpdateManifest.Parse(Envelope(PlainManifest(scheme: "http")), FixtureKey);
        Check(plainHttp.Windows.Count == 0, "an http package address must be refused");

        Check(!AppUpdateManifest.Allowed("http://updates.example/latest.json"), "http must not be allowed");
        Check(!AppUpdateManifest.Allowed("file:///tmp/latest.json"), "a file url must not be allowed");
        Check(!AppUpdateManifest.Allowed("https:///latest.json"), "an https url without a host must not be allowed");
        Check(AppUpdateManifest.Allowed("https://updates.example/latest.json"), "an https url must be allowed");
        return Task.CompletedTask;
    }

    /// 1.2.0 > 1.2.0-beta.2 > 1.1.9, missing components read as 0, a leading v allowed.
    internal static Task VersionComparisonOrdersReleasesAndPreReleases()
    {
        Check(AppVersion.IsNewer("1.2.0", "1.2.0-beta.2"), "a release must beat its pre-release");
        Check(AppVersion.IsNewer("1.2.0-beta.2", "1.1.9"), "a pre-release must beat the older release");
        Check(AppVersion.IsNewer("1.2", "1.1.9"), "a missing component must read as 0");
        Check(!AppVersion.IsNewer("1.2.0", "1.2"), "1.2 and 1.2.0 are the same version");
        Check(AppVersion.Normalized("v1.2.3") == "1.2.3", "a leading v must be dropped");
        Check(AppVersion.Normalized("1.2.3-beta.1") == "1.2.3-beta.1", "a pre-release tag must survive");
        Check(AppVersion.Normalized("not-a-version") is null, "a non-version must be refused");
        Check(AppVersion.Normalized(new string('9', 80)) is null, "an over-long version must be refused");
        return Task.CompletedTask;
    }

    // — Transport —

    private sealed class FakeHttp(Func<Uri, HttpResponseMessage> respond) : HttpMessageHandler
    {
        internal readonly List<Uri> Requested = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(respond(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok(byte[] body) =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };

    private static HttpResponseMessage RedirectTo(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private const string ManifestUrl = "https://updates.example/latest.json";

    /// Windows rule 1: a build with no public key never checks, and the section
    /// shows one sentence with the button disabled.
    internal static async Task NoPublicKeyMeansNoCheckAtAll()
    {
        var directory = Temp();
        try
        {
            var handler = new FakeHttp(_ => Ok(Envelope(PlainManifest())));
            using var service = new AppUpdateService(directory, publicKey: null, handler);
            Check(!service.VerifiesSignatures, "a build without a key must not claim to verify signatures");
            var message = await Refused(() => service.CheckAsync(ManifestUrl, "0.1.0"), "a key-less build must refuse to check");
            Check(message == AppUpdateStrings.NoPublicKeyNotice, "the refusal must be the section's own sentence");
            Check(handler.Requested.Count == 0, "a key-less build must not even ask for the manifest");

            // A key that is not 32 raw bytes is the same as no key.
            using var stubby = new AppUpdateService(directory, [1, 2, 3], handler);
            Check(!stubby.VerifiesSignatures, "a malformed key must read as no key");

            var coordinator = new AppUpdateCoordinator(service, "0.1.0", builtInManifestUrl: ManifestUrl);
            await coordinator.CheckAsync(null);
            var view = coordinator.Describe(_ => "00:00");
            Check(!view.SectionEnabled, "the section must be disabled without a key");
            Check(!view.ButtonEnabled, "the check button must be disabled without a key");
            Check(view.StatusText == AppUpdateStrings.NoPublicKeyNotice, "the one status sentence must be shown");
            Check(handler.Requested.Count == 0, "the coordinator must not reach the network without a key");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// Windows rule 3: the build's address wins; a user-entered address is
    /// ignored rather than preferred. Without a built-in address a typed https
    /// address is used and a non-https one is refused.
    internal static async Task ABuiltInAddressIgnoresAUserAddress()
    {
        var directory = Temp();
        try
        {
            var handler = new FakeHttp(_ => Ok(Envelope(PlainManifest())));
            using var service = new AppUpdateService(directory, FixtureKey, handler);

            var stamped = new AppUpdateCoordinator(service, "0.1.0", builtInManifestUrl: ManifestUrl);
            Check(stamped.EffectiveManifestUrl("https://attacker.example/latest.json") == ManifestUrl,
                "a build with an address must ignore a user address");
            Check(stamped.Describe(_ => "00:00").BuiltInAddressLine ==
                AppUpdateStrings.BuiltInAddressTemplate.Replace("{address}", ManifestUrl),
                "the built-in address line must name the address the build carries");
            Check(!stamped.Describe(_ => "00:00").AddressFieldEnabled,
                "the address field must be disabled when the build carries an address");

            await stamped.CheckAsync("https://attacker.example/latest.json");
            Check(handler.Requested.Single().ToString() == ManifestUrl, "only the built-in address may be requested");

            var open = new AppUpdateCoordinator(service, "0.1.0");
            Check(open.EffectiveManifestUrl("https://typed.example/latest.json") == "https://typed.example/latest.json",
                "without a built-in address a typed https address is used");
            Check(open.EffectiveManifestUrl("http://typed.example/latest.json") is null, "a typed http address is refused");
            Check(open.EffectiveManifestUrl("  ") is null, "an empty address is refused");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// Transport is https only, including every redirect hop.
    internal static async Task TransportRefusesANonHttpsHop()
    {
        var directory = Temp();
        try
        {
            var signed = Envelope(PlainManifest());
            var downgrade = new FakeHttp(url => url.ToString() == ManifestUrl
                ? RedirectTo("http://updates.example/latest.json")
                : Ok(signed));
            using (var service = new AppUpdateService(directory, FixtureKey, downgrade))
            {
                var message = await Refused(() => service.CheckAsync(ManifestUrl, "0.1.0"), "an http hop must be refused");
                Check(message.Contains("https"), "the refusal must name the transport rule: " + message);
                Check(downgrade.Requested.Count == 1, "the http hop must not be taken");
            }

            var mirrored = new FakeHttp(url => url.ToString() == ManifestUrl
                ? RedirectTo("https://mirror.example/latest.json")
                : Ok(signed));
            using (var service = new AppUpdateService(directory, FixtureKey, mirrored))
            {
                var availability = await service.CheckAsync(ManifestUrl, "0.1.0");
                Check(availability.IsNewer, "an https hop must be followed");
                Check(mirrored.Requested.Count == 2, "the https hop must be taken exactly once");
            }

            using (var service = new AppUpdateService(directory, FixtureKey, new FakeHttp(_ => Ok(signed))))
            {
                await Refused(() => service.CheckAsync("http://updates.example/latest.json", "0.1.0"),
                    "an http manifest address must be refused");
                await Refused(() => service.CheckAsync("file:///tmp/latest.json", "0.1.0"),
                    "a file manifest address must be refused");
            }
        }
        finally { Directory.Delete(directory, true); }
    }

    // — Download —

    private static string Sha256Of(byte[] bytes) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();

    /// The bytes on disk are checked against size and sha256; a mismatch, an
    /// oversized body and a cancelled download all leave nothing behind, and
    /// other version folders are removed first.
    internal static async Task DownloadVerifiesTheBytesOnDiskAndCleansUp()
    {
        var directory = Temp();
        try
        {
            var package = Encoding.UTF8.GetBytes(new string('p', 5000));
            var url = "https://updates.example/9.9.9/MightyClaude-windows-x64.zip";
            var handler = new FakeHttp(_ => Ok(package));
            using var service = new AppUpdateService(directory, FixtureKey, handler);

            // A folder for an older version must be gone after the download.
            var stale = Path.Combine(service.UpdatesDirectory, "1.0.0");
            Directory.CreateDirectory(stale);
            await File.WriteAllTextAsync(Path.Combine(stale, "old.zip"), "stale");

            var good = new AppUpdateAsset(url, Sha256Of(package), package.Length);
            var seen = new List<double>();
            var path = await service.DownloadAsync(good, "9.9.9", fraction => seen.Add(fraction));
            Check(File.Exists(path), "the verified package must be on disk");
            Check(new FileInfo(path).Length == package.Length, "the package on disk must be the package");
            Check(!Directory.Exists(stale), "another version folder must be removed");
            Check(seen.Count > 0 && Math.Abs(seen[^1] - 1) < 0.0001, "progress must end at 1");

            var wrongDigest = new AppUpdateAsset(url, new string('c', 64), package.Length);
            var digestMessage = await Refused(() => service.DownloadAsync(wrongDigest, "9.9.9"), "a wrong sha256 must be refused");
            Check(digestMessage.Contains("SHA-256"), "the refusal must name the digest: " + digestMessage);
            Check(!Directory.Exists(Path.Combine(service.UpdatesDirectory, "9.9.9")), "a mismatching download must be deleted");

            var wrongSize = new AppUpdateAsset(url, Sha256Of(package), package.Length - 1);
            await Refused(() => service.DownloadAsync(wrongSize, "9.9.9"), "a body longer than size must be refused");
            Check(!Directory.Exists(Path.Combine(service.UpdatesDirectory, "9.9.9")), "an oversized download must be deleted");

            var overCap = new AppUpdateAsset(url, Sha256Of(package), AppUpdateService.MaxPackageBytes + 1);
            var capMessage = await Refused(() => service.DownloadAsync(overCap, "9.9.9"), "an asset over the cap must be refused");
            Check(capMessage.Contains("너무 큽니다"), "the refusal must name the cap: " + capMessage);

            await Refused(() => service.DownloadAsync(good with { Url = "http://updates.example/p.zip" }, "9.9.9"),
                "an http package address must be refused");

            // A cancelled download leaves nothing behind.
            using var cancelled = new CancellationTokenSource();
            await cancelled.CancelAsync();
            await Refused(() => service.DownloadAsync(good, "9.9.9", null, cancelled.Token), "a cancelled download must not finish");
            Check(!Directory.Exists(Path.Combine(service.UpdatesDirectory, "9.9.9")), "a cancelled download must leave nothing behind");
        }
        finally { Directory.Delete(directory, true); }
    }

    // — Staging —

    /// A minimal PE header: e_lfanew at 0x3c, then the signature and the machine.
    private static byte[] FakeExecutable(string architecture)
    {
        var bytes = new byte[0x100];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(bytes, 0x3c);
        bytes[0x80] = (byte)'P'; bytes[0x81] = (byte)'E';
        BitConverter.GetBytes((ushort)(architecture == "arm64" ? 0xaa64 : 0x8664)).CopyTo(bytes, 0x84);
        return bytes;
    }

    private static string MakeZip(string folder, string name, Action<ZipArchive> fill)
    {
        var path = Path.Combine(folder, name);
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        fill(zip);
        return path;
    }

    private static void AddEntry(ZipArchive zip, string name, byte[] bytes, int externalAttributes = 0)
    {
        var entry = zip.CreateEntry(name);
        entry.ExternalAttributes = externalAttributes;
        using var stream = entry.Open();
        stream.Write(bytes);
    }

    /// Staging refuses an entry that escapes its folder, a link entry, a package
    /// without exactly one MightyClaude.exe at the root, and another architecture.
    internal static Task StagingRefusesAnEscapingEntryOrTheWrongPackage()
    {
        var directory = Temp();
        try
        {
            var exe = FakeExecutable("x64");
            var good = MakeZip(directory, "good.zip", zip =>
            {
                AddEntry(zip, "MightyClaude.exe", exe);
                AddEntry(zip, "Assets/mightyclaude.png", "png"u8.ToArray());
            });
            var staged = AppUpdateService.Stage(good, "x64");
            Check(File.Exists(Path.Combine(staged, "MightyClaude.exe")), "the executable must be staged at the root");
            Check(File.Exists(Path.Combine(staged, "Assets", "mightyclaude.png")), "a nested file must be staged");

            void Refuse(string name, Action<ZipArchive> fill, string architecture, string what)
            {
                var path = MakeZip(directory, name, fill);
                try { AppUpdateService.Stage(path, architecture); }
                catch (InvalidOperationException) { return; }
                throw new InvalidOperationException(what + " must be refused");
            }

            Refuse("escape.zip", zip =>
            {
                AddEntry(zip, "MightyClaude.exe", exe);
                AddEntry(zip, "../escaped.txt", "nope"u8.ToArray());
            }, "x64", "an entry with a .. segment");

            Refuse("absolute.zip", zip =>
            {
                AddEntry(zip, "MightyClaude.exe", exe);
                AddEntry(zip, "/etc/evil", "nope"u8.ToArray());
            }, "x64", "an entry with an absolute path");

            // S_IFLNK (0xA000) in the high 16 bits marks a symbolic link.
            Refuse("link.zip", zip =>
            {
                AddEntry(zip, "MightyClaude.exe", exe);
                AddEntry(zip, "shortcut", "/etc/passwd"u8.ToArray(), unchecked((int)0xa1ff0000));
            }, "x64", "a link entry");

            Refuse("none.zip", zip => AddEntry(zip, "Readme.txt", "hello"u8.ToArray()), "x64",
                "a package with no MightyClaude.exe");

            Refuse("two.zip", zip =>
            {
                AddEntry(zip, "MightyClaude.exe", exe);
                AddEntry(zip, "tools/MightyClaude.exe", exe);
            }, "x64", "a package with two executables");

            Refuse("nested.zip", zip => AddEntry(zip, "app/MightyClaude.exe", exe), "x64",
                "an executable that is not at the root");

            Refuse("arm.zip", zip => AddEntry(zip, "MightyClaude.exe", FakeExecutable("arm64")), "x64",
                "a package for another architecture");

            var armPackage = MakeZip(directory, "arm-ok.zip", zip => AddEntry(zip, "MightyClaude.exe", FakeExecutable("arm64")));
            Check(Directory.Exists(AppUpdateService.Stage(armPackage, "arm64")), "an arm64 package must stage on arm64");
            return Task.CompletedTask;
        }
        finally { Directory.Delete(directory, true); }
    }

    // — Replacement —

    private static void MakeInstall(string path, string marker)
    {
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "MightyClaude.exe"), marker);
    }

    /// The helper verifies the package again immediately before it replaces the
    /// install, replaces it, starts the new app and only then removes the backup.
    internal static async Task ReplacementVerifiesAgainAndReplacesTheInstall()
    {
        var root = Temp();
        try
        {
            var install = Path.Combine(root, "MightyClaude");
            var staged = Path.Combine(root, "staged");
            MakeInstall(install, "old");
            MakeInstall(staged, "new");
            var packageBytes = "package"u8.ToArray();
            var package = Path.Combine(root, "package.zip");
            await File.WriteAllBytesAsync(package, packageBytes);

            var plan = AppUpdateInstallPlan.Create(staged, install, package, Sha256Of(packageBytes), packageBytes.Length, 4242);
            Check(plan.IsValid, "the plan must be valid");
            Check(plan.HelperArguments[0] == AppUpdateInstallPlan.HelperFlag, "the helper argument list must start with the flag");
            Check(AppUpdateInstallPlan.TryParse(plan.HelperArguments) == plan with { PreviousExecutable = plan.PreviousExecutable },
                "the helper must rebuild the same plan from its arguments");
            Check(AppUpdateReplacement.Plan(plan).Select(move => move.Kind).SequenceEqual(["backup", "install"]),
                "the plan of moves is backup then install");
            Check(AppUpdateReplacement.Rollback(plan).Kind == "rollback", "the rollback move undoes the backup");
            Check(AppUpdateReplacement.ShouldRollback(true, false), "a taken backup with no install must roll back");
            Check(!AppUpdateReplacement.ShouldRollback(true, true), "a finished install must not roll back");
            Check(!AppUpdateInstallPlan.HelperEnvironment.Keys.Any(key =>
                key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("HOME", StringComparison.OrdinalIgnoreCase) ||
                key.Contains("USER", StringComparison.OrdinalIgnoreCase)),
                "the helper environment must carry nothing about the user");

            var backupPresentWhenStarted = false;
            var started = new List<string>();
            var result = await AppUpdateReplacement.RunAsync(
                plan,
                _ => Task.FromResult(true),
                path =>
                {
                    backupPresentWhenStarted = Directory.Exists(plan.BackupDirectory);
                    started.Add(path);
                    return Task.CompletedTask;
                });

            Check(result.Replaced && !result.RolledBack, "the replacement must succeed: " + result.Error);
            Check(await File.ReadAllTextAsync(Path.Combine(install, "MightyClaude.exe")) == "new", "the new app must be installed");
            Check(!Directory.Exists(staged), "the staged folder must have been moved, not copied");
            Check(started.SequenceEqual([Path.Combine(install, "MightyClaude.exe")]), "the new app must be started once");
            Check(backupPresentWhenStarted, "the backup must still exist when the new app starts");
            Check(!Directory.Exists(plan.BackupDirectory), "the backup must be removed once the new app has started");
            Check(result.Moves.Select(move => move.Kind).SequenceEqual(["backup", "install"]), "the moves must be the plan");
        }
        finally { Directory.Delete(root, true); }
    }

    /// A package whose digest changed after the download, an app that never
    /// quits, and a failed swap all leave the user on the version they had.
    internal static async Task ReplacementLeavesTheInstallUntouchedWhenItCannotProceed()
    {
        var root = Temp();
        try
        {
            var install = Path.Combine(root, "MightyClaude");
            var staged = Path.Combine(root, "staged");
            MakeInstall(install, "old");
            MakeInstall(staged, "new");
            var package = Path.Combine(root, "package.zip");
            await File.WriteAllBytesAsync(package, "package"u8.ToArray());
            var plan = AppUpdateInstallPlan.Create(staged, install, package, Sha256Of("package"u8.ToArray()), 7, 4242);

            // The package is swapped underneath after the download verified it.
            await File.WriteAllBytesAsync(package, "swapped"u8.ToArray());
            var tampered = await AppUpdateReplacement.RunAsync(plan, _ => Task.FromResult(true), _ => Task.CompletedTask);
            Check(!tampered.Replaced && !tampered.RolledBack, "a changed package must not be installed");
            Check(tampered.Error!.Contains("재검증"), "the refusal must name the re-verification: " + tampered.Error);
            Check(await File.ReadAllTextAsync(Path.Combine(install, "MightyClaude.exe")) == "old", "the install must be untouched");
            Check(tampered.Moves.Count == 0, "nothing may be moved when the digest differs");

            await File.WriteAllBytesAsync(package, "package"u8.ToArray());
            var stillRunning = await AppUpdateReplacement.RunAsync(plan, _ => Task.FromResult(false), _ => Task.CompletedTask);
            Check(!stillRunning.Replaced, "the swap must not start while the app runs");
            Check(await File.ReadAllTextAsync(Path.Combine(install, "MightyClaude.exe")) == "old", "the install must be untouched");

            // Starting the new app fails: the old one is put back and started.
            var restarted = new List<string>();
            var rolled = await AppUpdateReplacement.RunAsync(
                plan,
                _ => Task.FromResult(true),
                path => { restarted.Add(path); throw new IOException("새 앱을 시작하지 못했습니다."); });
            Check(!rolled.Replaced && rolled.RolledBack, "a failed start must roll back: " + rolled.Error);
            Check(await File.ReadAllTextAsync(Path.Combine(install, "MightyClaude.exe")) == "old", "the old app must be back");
            Check(rolled.Moves.Select(move => move.Kind).SequenceEqual(["backup", "install", "rollback"]),
                "the rollback move must be recorded");
            Check(restarted.Count == 2, "the old app must be started after the rollback");

            Check(AppUpdateInstallPlan.TryParse(["--update-helper", "--staged", staged]) is null,
                "a helper argument list missing values must not produce a plan");
            Check(AppUpdateInstallPlan.TryParse(["--smoke-test"]) is null, "another argument list must not produce a plan");
        }
        finally { Directory.Delete(root, true); }
    }

    // — Automatic check and the section —

    /// The automatic check is a saved preference with the macOS default, at
    /// most once a day, and never without a public key.
    internal static async Task AutomaticCheckHappensAtMostOnceADay()
    {
        Check(new AppSnapshot().AppUpdateAutoCheck, "the macOS default is on");
        Check(new AppSnapshot().Version == 1, "the snapshot version must stay 1");
        var round = JsonSerializer.Deserialize<AppSnapshot>(
            JsonSerializer.Serialize(new AppSnapshot { AppUpdateAutoCheck = false }, Wire.Json), Wire.Json)!;
        Check(!round.AppUpdateAutoCheck, "the saved preference must survive a round trip");
        Check(JsonSerializer.Deserialize<AppSnapshot>("{\"version\":1}", Wire.Json)!.AppUpdateAutoCheck,
            "a file written before the field existed must load with the macOS default");

        var now = DateTimeOffset.UtcNow;
        Check(AppUpdateCoordinator.IsDue(true, true, null, now), "a first run is due");
        Check(AppUpdateCoordinator.IsDue(true, true, now.AddHours(-25), now), "a check older than a day is due");
        Check(!AppUpdateCoordinator.IsDue(true, true, now.AddHours(-23), now), "a check inside the day is not due");
        Check(!AppUpdateCoordinator.IsDue(false, true, null, now), "the preference off means never");
        Check(!AppUpdateCoordinator.IsDue(true, false, null, now), "no public key means never");

        var directory = Temp();
        try
        {
            var handler = new FakeHttp(_ => Ok(Envelope(PlainManifest())));
            using var service = new AppUpdateService(directory, FixtureKey, handler);
            var coordinator = new AppUpdateCoordinator(service, "0.1.0", builtInManifestUrl: ManifestUrl);
            Check(await coordinator.CheckAutomaticallyIfDueAsync(null, true, null, now), "the first automatic check runs");
            Check(coordinator.State.Phase == AppUpdatePhase.Available, "the automatic check must find the new version");
            Check(!await coordinator.CheckAutomaticallyIfDueAsync(null, true, now, now), "a second check inside the day is skipped");
            Check(handler.Requested.Count == 1, "only one manifest request may be made");
        }
        finally { Directory.Delete(directory, true); }
    }

    /// The section shows the macOS copy for every phase and offers download,
    /// cancel and install in the right order.
    internal static async Task SectionShowsTheMacOSCopyForEveryPhase()
    {
        var statuses = AppUpdateSmoke.FixtureStates
            .Select(state => AppUpdatePresentation.Describe(state, true, null, _ => "00:00").StatusText).ToArray();
        var buttons = AppUpdateSmoke.FixtureStates
            .Select(state => AppUpdatePresentation.Describe(state, true, null, _ => "00:00").ButtonLabel).ToArray();

        Check(statuses[0] == AppUpdateStrings.Checking, "the checking sentence must be the macOS one");
        Check(statuses[1] == "새 버전 9.9.9 이 있습니다.", "the available sentence must name the version");
        Check(statuses[2] == "42% 받는 중…", "the download sentence must carry the percentage");
        Check(statuses[3] == AppUpdateStrings.StagingProgress, "the staging sentence must be the macOS one");
        Check(statuses[5] == AppUpdateStrings.Installing, "the installing sentence must be the macOS one");
        Check(buttons.SequenceEqual([
            AppUpdateStrings.InProgressButton, AppUpdateStrings.DownloadButton, AppUpdateStrings.CancelButton,
            AppUpdateStrings.InProgressButton, AppUpdateStrings.InstallButton, AppUpdateStrings.InProgressButton]),
            "the buttons must follow the macOS order: " + string.Join(", ", buttons));

        var idle = AppUpdatePresentation.Describe(new(), true, null, _ => "00:00");
        Check(idle.StatusText == AppUpdateStrings.NotCheckedYet, "an unchecked build says so");
        Check(idle.ButtonLabel == AppUpdateStrings.CheckButton && idle.ButtonEnabled, "the check button is offered when idle");
        Check(idle.SignatureNotice == AppUpdateStrings.SignatureVerified, "a build with a key says it verifies signatures");
        var checkedAt = AppUpdatePresentation.Describe(
            new() { CheckedAt = DateTimeOffset.UnixEpoch }, true, null, _ => "09:30");
        Check(checkedAt.StatusText == "마지막 확인 09:30", "the last check time is shown");
        Check(AppUpdatePresentation.Describe(
            new() { Phase = AppUpdatePhase.Available, Availability = AppUpdateSmoke.FixtureStates[1].Availability },
            true, null, _ => "00:00").Notes == null, "fixture notes are absent, so no note line is shown");

        // The smoke decision accepts what the section renders and refuses less.
        var current = true;
        var outcome = await AppUpdateSmoke.RunAsync(
            AppUpdateSmoke.ExpectedStatuses, AppUpdateSmoke.ExpectedButtons, true,
            () => current, value => { current = value; return Task.CompletedTask; });
        Check(outcome.Restored && current, "the smoke run must put the switch back");
        await Refused(() => AppUpdateSmoke.RunAsync([], AppUpdateSmoke.ExpectedButtons, true, () => current,
            value => { current = value; return Task.CompletedTask; }), "a section that rendered nothing must fail the smoke run");
        await Refused(() => AppUpdateSmoke.RunAsync(AppUpdateSmoke.ExpectedStatuses, AppUpdateSmoke.ExpectedButtons, false,
            () => current, value => { current = value; return Task.CompletedTask; }),
            "a key-less build that still checked must fail the smoke run");
    }

    /// The whole pipeline, end to end, against a fixture-signed manifest: check,
    /// download for this machine's architecture, stage, and hand a plan to the
    /// helper that re-verifies before it replaces.
    internal static async Task PipelineRunsFromAFixtureSignedManifestToAReadyInstallPlan()
    {
        var root = Temp();
        try
        {
            var state = Path.Combine(root, "state");
            var install = Path.Combine(root, "MightyClaude");
            MakeInstall(install, "old");

            var packagePath = MakeZip(root, "MightyClaude-windows-x64.zip",
                zip => AddEntry(zip, "MightyClaude.exe", FakeExecutable("x64")));
            var packageBytes = await File.ReadAllBytesAsync(packagePath);
            var manifest = "{\"version\":\"9.9.9\",\"windows\":{\"x64\":{"
                + "\"url\":\"https://updates.example/9.9.9/MightyClaude-windows-x64.zip\","
                + $"\"sha256\":\"{Sha256Of(packageBytes)}\",\"size\":{packageBytes.Length}}}}}}}";

            var handler = new FakeHttp(url => Ok(url.ToString() == ManifestUrl ? Envelope(manifest) : packageBytes));
            using var service = new AppUpdateService(state, FixtureKey, handler);
            var launched = new List<AppUpdateInstallPlan>();
            var coordinator = new AppUpdateCoordinator(
                service, "0.1.0", ManifestUrl, "x64", install, plan => { launched.Add(plan); return Task.CompletedTask; });

            await coordinator.PressAsync(null);
            Check(coordinator.State.Phase == AppUpdatePhase.Available, "the check must offer the new version: " + coordinator.State.ErrorMessage);

            await coordinator.PressAsync(null);
            Check(coordinator.State.Phase == AppUpdatePhase.Ready, "the download must end ready: " + coordinator.State.ErrorMessage);
            Check(coordinator.State.InstallPlan is not null, "a ready update must carry an install plan");
            Check(File.Exists(Path.Combine(coordinator.State.StagedDirectory!, "MightyClaude.exe")), "the package must be staged");

            await coordinator.PressAsync(null);
            Check(coordinator.State.Phase == AppUpdatePhase.Installing, "the install must hand over to the helper");
            Check(launched.Count == 1 && launched[0].InstallDirectory == install, "the helper must be given the install folder");

            // The helper re-verifies and replaces, using the plan the app built.
            var result = await AppUpdateReplacement.RunAsync(launched[0], _ => Task.FromResult(true), _ => Task.CompletedTask);
            Check(result.Replaced, "the helper must replace the install: " + result.Error);
            Check(File.Exists(Path.Combine(install, "MightyClaude.exe")), "the new app must be in place");

            // A manifest with no asset for this machine is refused.
            var armOnly = new AppUpdateCoordinator(service, "0.1.0", ManifestUrl, "arm64", install);
            await armOnly.PressAsync(null);
            await armOnly.PressAsync(null);
            Check(armOnly.State.Phase == AppUpdatePhase.Failed, "a manifest without this architecture must fail");
            Check(armOnly.State.ErrorMessage!.Contains("arm64"), "the refusal must name the architecture");
        }
        finally { Directory.Delete(root, true); }
    }
}
