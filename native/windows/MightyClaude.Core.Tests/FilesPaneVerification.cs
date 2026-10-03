using System.Text;
using MightyClaude.Core;

/// <summary>
/// The read-only files pane (docs/file-pane.md) on Windows: the same vectors as macOS
/// WorkspaceFilesTests and SourceHighlighterTests, plus the Windows pane rules
/// (Ctrl+Shift+E, one pane per workspace placed left, never saved).
/// </summary>
internal static class FilesPaneVerification
{
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static (string Root, string Outside) MakeRoot()
    {
        var parent = Verification.Temp();
        var root = Path.Combine(parent, "files-root");
        var outside = Path.Combine(parent, "files-outside");
        Directory.CreateDirectory(root); Directory.CreateDirectory(outside);
        return (root, outside);
    }

    private static void Write(string text, string path, string root) => WriteBytes(Encoding.UTF8.GetBytes(text), path, root);
    private static void WriteBytes(byte[] bytes, string path, string root)
    {
        var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    /// <summary>Makes a link; false where the OS refuses (a Windows account without the symlink right).</summary>
    private static bool Link(string at, string target, bool directory)
    {
        try { if (directory) Directory.CreateSymbolicLink(at, target); else File.CreateSymbolicLink(at, target); return true; }
        catch (Exception ex) when (OperatingSystem.IsWindows() && ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void Cleanup(string root, string outside) { try { Directory.Delete(Path.GetDirectoryName(root)!, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    internal static Task TreeSortsFoldersFirstInNaturalOrderAndKeepsNoiseCollapsed()
    {
        var (root, outside) = MakeRoot();
        try
        {
            foreach (var name in new[] { "file10.txt", "file2.txt", "b.swift", "A.md", ".env", ".gitignore" }) Write("x", name, root);
            foreach (var folder in new[] { "src", "Docs", "node_modules", ".git" }) Directory.CreateDirectory(Path.Combine(root, folder));
            if (OperatingSystem.IsWindows()) File.SetAttributes(Path.Combine(root, ".env"), FileAttributes.Hidden);
            var listing = WorkspaceFiles.List("", root);
            Check(listing.Entries.Select(e => e.Name).SequenceEqual([".git", "Docs", "node_modules", "src", ".env", ".gitignore", "A.md", "b.swift", "file2.txt", "file10.txt"]),
                "folders first, natural case-insensitive order, hidden files shown: " + string.Join(",", listing.Entries.Select(e => e.Name)));
            Check(!listing.Truncated, "a small folder is not truncated");
            Check(listing.Entries.Where(e => e.IsNoise).Select(e => e.Name).SequenceEqual([".git", "node_modules"]), "noise folders are marked");
            Check(!new WorkspaceFileEntry("build", "build", false).IsNoise, "a file named build is not a noise folder");
            foreach (var name in new[] { ".git", "node_modules", ".build", "build", "dist", "DerivedData", ".next", "Pods", ".venv", "__pycache__" })
                Check(WorkspaceFiles.IsNoiseFolder(name), name + " is a noise folder as on macOS");
            Write("x", "src/inner/deep.swift", root);
            Check(WorkspaceFiles.List("src", root).Entries.Select(e => e.RelativePath).SequenceEqual(["src/inner"]), "nested listing uses / relative paths");
            Check(WorkspaceFiles.List("src/inner", root).Entries.Select(e => e.RelativePath).SequenceEqual(["src/inner/deep.swift"]), "deeper listing");
            var sorted = WorkspaceFiles.Sorted([new("z10", "z10", false), new("Z2", "Z2", false), new("lib", "lib", true), new("App", "App", true)]);
            Check(sorted.Select(e => e.Name).SequenceEqual(["App", "lib", "Z2", "z10"]), "pure sort matches macOS");
        }
        finally { Cleanup(root, outside); }
        return Task.CompletedTask;
    }

    internal static Task ListingStopsAtItsCeilingAndSaysItIsTruncated()
    {
        var (root, outside) = MakeRoot();
        try
        {
            for (var index = 0; index < 30; index++) Write("x", $"f{index}.txt", root);
            var capped = WorkspaceFiles.List("", root, enumerationLimit: 12);
            Check(capped.Entries.Count == 12 && capped.Truncated, "12 names read, listing truncated");
            var exact = WorkspaceFiles.List("", root, enumerationLimit: 30);
            Check(exact.Entries.Count == 30 && !exact.Truncated, "exactly the limit is not truncated");
            Check(WorkspaceFiles.MaximumEnumeratedNames == 20_000 && WorkspaceFiles.MaximumEntriesPerFolder == 5_000, "the macOS caps");
            Directory.CreateDirectory(Path.Combine(root, "한글 폴더"));
            Check(WorkspaceFiles.List("", root).Entries[0].Name == "한글 폴더", "a Korean folder name lists first among folders");
        }
        finally { Cleanup(root, outside); }
        return Task.CompletedTask;
    }

    internal static Task PathsAndLinksThatLeaveTheWorkspaceAreRefused()
    {
        var (root, outside) = MakeRoot();
        try
        {
            Write("secret", "secret.txt", outside);
            Write("inside", "docs/readme.md", root);
            var outsideName = Path.GetFileName(outside);
            Check(WorkspaceFiles.Resolve("../" + outsideName + "/secret.txt", root) is null, "a .. path out of the root is refused");
            Check(WorkspaceFiles.Resolve("docs/../../" + outsideName, root) is null, "docs/../.. is refused");
            Check(WorkspaceFiles.Resolve(outside, root) is null, "an absolute path is refused");
            Check(WorkspaceFiles.Resolve("docs/readme.md:secret", root) is null, "a Windows stream name is refused");
            Check(WorkspaceFiles.Resolve("docs\0x", root) is null, "NUL is refused");
            Check(WorkspaceFiles.Resolve("docs/readme.md", root) is not null, "a file inside resolves");
            Check(WorkspaceFiles.Resolve("", root) == WorkspaceFiles.RealPath(root), "\"\" is the real root");
            Check(Throws<WorkspaceFileException>(() => WorkspaceFiles.List("docs/readme.md", root), e => e.Error == WorkspaceFileError.NotDirectory), "listing a file says not a directory");
            Check(Throws<WorkspaceFileException>(() => WorkspaceFiles.List("../" + outsideName, root), e => e.Error == WorkspaceFileError.OutsideRoot), "listing outside says outside the root");

            var links = Link(Path.Combine(root, "escape.txt"), Path.Combine(outside, "secret.txt"), false)
                & Link(Path.Combine(root, "outdir"), outside, true)
                & Link(Path.Combine(root, "alias"), Path.Combine(root, "docs"), true)
                & Link(Path.Combine(root, "dangling"), Path.Combine(root, "missing"), false);
            if (links)
            {
                var entries = WorkspaceFiles.List("", root).Entries;
                Check(entries.Select(e => e.Name).SequenceEqual(["alias", "docs"]), "escaping and dangling links are not listed: " + string.Join(",", entries.Select(e => e.Name)));
                var alias = entries.First(e => e.Name == "alias");
                Check(alias.IsDirectory && alias.IsSymlink, "an in-root folder link shows as a linked folder");
                Check(WorkspaceFiles.List("alias", root).Entries.Select(e => e.RelativePath).SequenceEqual(["alias/readme.md"]), "an in-root link lists under its own name");
                Check(WorkspaceFiles.Resolve("escape.txt", root) is null && WorkspaceFiles.Resolve("outdir", root) is null && WorkspaceFiles.Resolve("outdir/secret.txt", root) is null, "links out of the root are refused");
                Check(WorkspaceFiles.Resolve("dangling", root) is null, "a dangling link is refused");
                Check(Throws<WorkspaceFileException>(() => WorkspaceFiles.List("outdir", root), e => e.Error == WorkspaceFileError.OutsideRoot), "listing a link out says outside the root");
                Check(Throws<WorkspaceFileOpenException>(() => WorkspaceFiles.OpenFile("escape.txt", root), e => e.Error == WorkspaceFileOpenError.Missing), "opening a link out is refused as missing");
            }
            else Console.WriteLine("  (symlink checks skipped: this Windows account cannot create links)");

            using (var opened = WorkspaceFiles.OpenFile("docs/readme.md", root))
                Check(FilePreviewClassifier.ReadHead(opened.Stream).SequenceEqual("inside"u8.ToArray()) && opened.Size == 6 && opened.Modified is not null, "a file inside opens and reads");
            Check(Throws<WorkspaceFileOpenException>(() => WorkspaceFiles.OpenFile("docs", root), e => e.Error == WorkspaceFileOpenError.NotRegularFile), "a folder is not a regular file");
            Check(Throws<WorkspaceFileOpenException>(() => WorkspaceFiles.OpenFile("gone.txt", root), e => e.Error == WorkspaceFileOpenError.Missing), "a missing file is missing");
        }
        finally { Cleanup(root, outside); }
        return Task.CompletedTask;
    }

    internal static Task ClassificationUsesNamesThenSniffing()
    {
        var text = "hello\n"u8.ToArray(); var binary = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00, 0x01 };
        Check(FilePreviewClassifier.Classify("README.md", text) == FilePreviewKind.Markdown, "md");
        Check(FilePreviewClassifier.Classify("page.MDX", text) == FilePreviewKind.Markdown, "MDX");
        Check(FilePreviewClassifier.Classify("App.swift", text) == FilePreviewKind.Source(SourceLanguage.Swift), "swift");
        Check(FilePreviewClassifier.Classify("index.tsx", text) == FilePreviewKind.Source(SourceLanguage.JavaScript), "tsx");
        Check(FilePreviewClassifier.Classify("main.rs", text) == FilePreviewKind.Source(SourceLanguage.Rust), "rust");
        Check(FilePreviewClassifier.Classify("build.gradle", text) == FilePreviewKind.Source(SourceLanguage.Gradle), "gradle");
        Check(FilePreviewClassifier.Classify("Dockerfile", text) == FilePreviewKind.Source(SourceLanguage.Dockerfile), "Dockerfile");
        Check(FilePreviewClassifier.Classify("Dockerfile.dev", text) == FilePreviewKind.Source(SourceLanguage.Dockerfile), "Dockerfile.dev");
        Check(FilePreviewClassifier.Classify("Makefile", text) == FilePreviewKind.Source(SourceLanguage.Makefile), "Makefile");
        Check(FilePreviewClassifier.Classify(".env", text) == FilePreviewKind.Source(SourceLanguage.Shell), ".env");
        Check(FilePreviewClassifier.Classify(".env.local", text) == FilePreviewKind.Source(SourceLanguage.Shell), ".env.local");
        Check(FilePreviewClassifier.Classify("data.csv", text) == FilePreviewKind.Source(SourceLanguage.Plain), "csv");
        Check(FilePreviewClassifier.Classify("LICENSE", text) == FilePreviewKind.Source(SourceLanguage.Plain), "sniffed text");
        Check(FilePreviewClassifier.Classify("Program.cs", text) == FilePreviewKind.Source(SourceLanguage.Plain), "unknown extension sniffed as text");
        Check(FilePreviewClassifier.Classify("photo.JPG", binary) == FilePreviewKind.Image, "JPG");
        Check(FilePreviewClassifier.Classify("logo.svg", text) == FilePreviewKind.Image, "svg");
        Check(FilePreviewClassifier.Classify("spec.pdf", binary) == FilePreviewKind.Image, "pdf");
        Check(FilePreviewClassifier.Classify("program", binary) == FilePreviewKind.Unsupported, "binary");
        Check(FilePreviewClassifier.Classify("Info.plist", "bplist00\0"u8.ToArray()) == FilePreviewKind.Unsupported, "binary plist");
        Check(FilePreviewClassifier.Classify("archive.zip", binary) == FilePreviewKind.Unsupported, "zip");
        Check(FilePreviewClassifier.Classify("empty", []) == FilePreviewKind.Source(SourceLanguage.Plain), "empty file is text");
        Check(FilePreviewClassifier.FileExtension(".gitignore") == "" && FilePreviewClassifier.FileExtension("a.b.TS") == "ts", "extensions");
        var korean = Encoding.UTF8.GetBytes("안녕");
        Check(FilePreviewClassifier.LooksLikeText(korean[..^1]), "a UTF-8 sample cut mid-character is text");
        Check(!FilePreviewClassifier.LooksLikeText([0x41, 0xC3, 0x28, 0xFF, 0x41]), "bad bytes are not text");
        return Task.CompletedTask;
    }

    internal static Task EncodingsFollowTheByteOrderMarkThenUtf8ThenCp949()
    {
        (string, TextEncodingKind)? Decoded(byte[] bytes) => FilePreviewClassifier.DecodeText(bytes, sample: false) is { } d ? (d.Text, d.Encoding) : null;
        Check(Decoded([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("가a")]) == ("가a", TextEncodingKind.Utf8Bom), "UTF-8 BOM");
        Check(Decoded([0xFF, 0xFE, 0x41, 0x00, 0x00, 0xAC]) == ("A가", TextEncodingKind.Utf16LE), "UTF-16 LE");
        Check(Decoded([0xFE, 0xFF, 0x00, 0x41, 0xAC, 0x00]) == ("A가", TextEncodingKind.Utf16BE), "UTF-16 BE");
        Check(Decoded([0xFF, 0xFE, 0x00, 0x00, 0x41, 0x00, 0x00, 0x00]) == ("A", TextEncodingKind.Utf32LE), "UTF-32 LE");
        Check(Decoded([0x00, 0x00, 0xFE, 0xFF, 0x00, 0x00, 0x00, 0x41]) == ("A", TextEncodingKind.Utf32BE), "UTF-32 BE");
        Check(FilePreviewClassifier.LooksLikeText([0xFF, 0xFE, 0x41, 0x00, 0x42]), "a UTF-16 sample cut mid-unit");
        Check(Decoded(Encoding.UTF8.GetBytes("plain 안녕")) == ("plain 안녕", TextEncodingKind.Utf8), "UTF-8");
        var korean = TextEncodings.Strict(TextEncodingKind.Cp949).GetBytes("안녕하세요 똠방각하 abc");
        Check(Decoded(korean) == ("안녕하세요 똠방각하 abc", TextEncodingKind.Cp949), "CP949");
        Check(FilePreviewClassifier.DecodeText(korean[..5], sample: true) is { Text: "안녕", Encoding: TextEncodingKind.Cp949 }, "a CP949 sample may end on a lead byte");
        Check(FilePreviewClassifier.Classify("notes.txt", korean) == FilePreviewKind.Source(SourceLanguage.Plain), "CP949 text is text");
        Check(FilePreviewClassifier.Classify("notes.txt", "a\0b"u8.ToArray()) == FilePreviewKind.Unsupported, "NUL without a mark is binary");
        Check(TextEncodings.DisplayName(TextEncodingKind.Cp949) == "CP949 (EUC-KR)" && TextEncodings.DisplayName(TextEncodingKind.Utf16LE) == "UTF-16 LE", "display names match macOS");

        var (root, outside) = MakeRoot();
        try
        {
            WriteBytes([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("line\n")], "wide.txt", root);
            WriteBytes(korean, "euckr.txt", root);
            using (var file = WorkspaceFiles.OpenFile("wide.txt", root)) { var wide = FilePreviewClassifier.ReadText(file.Stream); Check(wide.Text == "line\n" && wide.Encoding == TextEncodingKind.Utf16LE, "UTF-16 file reads"); }
            using (var file = WorkspaceFiles.OpenFile("euckr.txt", root)) { var legacy = FilePreviewClassifier.ReadText(file.Stream); Check(legacy.Text == "안녕하세요 똠방각하 abc" && legacy.Encoding == TextEncodingKind.Cp949, "CP949 file reads"); }
            using (var file = WorkspaceFiles.OpenFile("euckr.txt", root)) { var cut = FilePreviewClassifier.ReadText(file.Stream, maximumBytes: 5); Check(cut.Truncated && cut.Text == "안녕" && cut.Encoding == TextEncodingKind.Cp949, "a cut CP949 file drops the half character"); }
            Write(string.Concat(Enumerable.Repeat("가", 10)), "big.txt", root);
            using (var file = WorkspaceFiles.OpenFile("big.txt", root))
            {
                var capped = FilePreviewClassifier.ReadText(file.Stream, maximumBytes: 7);
                Check(capped.Truncated && capped.Text == "가가" && capped.Encoding == TextEncodingKind.Utf8, "text stops at the cap");
                var whole = FilePreviewClassifier.ReadText(file.Stream);
                Check(!whole.Truncated && whole.Text.Length == 10 && FilePreviewClassifier.ReadHead(file.Stream).Length == 30, "the whole text reads from the start again");
                Check(FilePreviewClassifier.MaximumTextBytes == 1_048_576, "1 MB text cap");
            }
        }
        finally { Cleanup(root, outside); }
        return Task.CompletedTask;
    }

    internal static Task PreviewLoaderShowsSourceMarkdownAndImagesWithTheMacCaps()
    {
        var (root, outside) = MakeRoot();
        try
        {
            Write("import Foundation\n\n// A comment\nlet answer = 42\nprint(\"hello\")\n", "Sources/App.swift", root);
            Write("# Files pane\n\n- rendered **markdown**\n", "README.md", root);
            Write(new string('x', FilePreviewClassifier.MaximumMarkdownRenderBytes + 1), "big.md", root);
            WriteBytes([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00], "image.png", root);
            Write("<svg xmlns=\"http://www.w3.org/2000/svg\"><image href=\"file:///etc/hosts\"/></svg>", "bad.svg", root);
            WriteBytes([0x00, 0x01, 0x02], "program.bin", root);

            var swift = FilePreviewLoader.Load(root, "Sources/App.swift");
            Check(swift.Kind == FilePreviewKind.Source(SourceLanguage.Swift) && swift.Encoding == TextEncodingKind.Utf8 && SourceLines.Scan(swift.Text!).Starts.Count == 6, "swift source with six line starts");
            var markdown = FilePreviewLoader.Load(root, "README.md");
            Check(markdown.Kind == FilePreviewKind.Markdown && markdown.MarkdownRenderable, "small markdown renders");
            var big = FilePreviewLoader.Load(root, "big.md");
            Check(big.Kind == FilePreviewKind.Markdown && !big.MarkdownRenderable && FilePreviewClassifier.MaximumMarkdownRenderBytes == 131_072, "markdown over 128 KB opens as source");
            var png = FilePreviewLoader.Load(root, "image.png");
            Check(png.Kind == FilePreviewKind.Image && png.ImageBytes?.Length == 9, "an image keeps its bytes for the decoder");
            var svg = FilePreviewLoader.Load(root, "bad.svg");
            Check(svg.Kind == FilePreviewKind.Unsupported && svg.Reason == Locale.Get("files.preview.svgExternal"), "an svg reading outside content is not drawn");
            Check(FilePreviewLoader.Load(root, "program.bin").Kind == FilePreviewKind.Unsupported, "binary is unsupported");
            Check(FilePreviewLoader.Load(root, "gone.txt").Failure == Locale.Get("files.preview.missing"), "a vanished file says missing");
            Check(FilePreviewClassifier.MaximumImageBytes == 50L * 1_048_576 && FilePreviewClassifier.MaximumDecodePixels == 250_000_000 && FilePreviewClassifier.MaximumFullPixels == 100_000_000 && FilePreviewClassifier.MaximumFitPixels == 4_096, "image caps match macOS");
            Check(FilePreviewClassifier.PixelCount(40, 30) == 1200 && FilePreviewClassifier.PixelCount(0, 3) is null && FilePreviewClassifier.PixelCount(long.MaxValue, 2) is null, "pixel counts refuse zero and overflow");
            Check(FilePreviewClassifier.IsDrawable(10, 10) && !FilePreviewClassifier.IsDrawable(double.PositiveInfinity, 1) && !FilePreviewClassifier.IsDrawable(1e30, 1), "vector sizes are bounded");
        }
        finally { Cleanup(root, outside); }
        return Task.CompletedTask;
    }

    internal static Task SvgThatLoadsOutsideContentIsRefused()
    {
        const string outside = "/tmp/files-outside/secret.png";
        const string head = "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" width=\"10\" height=\"10\">";
        string[] external =
        [
            $"<image href=\"file://{outside}\" width=\"10\" height=\"10\"/>",
            $"<image xlink:href=\"{outside}\" width=\"10\" height=\"10\"/>",
            "<image href=\"secret.png\" width=\"10\" height=\"10\"/>",
            "<image href=\"../files-outside/secret.png\"/>",
            "<IMAGE XLINK:HREF = ' file:///etc/hosts ' />",
            $"<image href\n=\n\"{outside}\"/>",
            $"<image href={outside} />",
            $"<image href=\"&#102;ile://{outside}\"/>",
            $"<image href=\"&#x66;ile://{outside}\"/>",
            "<image href=\"data:image/svg+xml;base64,PHN2Zy8+\"/>",
            "<image href=\"\"/>",
            "<rect style=\"fill:url(other.svg#paint)\" width=\"5\" height=\"5\"/>",
            "<rect fill=\"URL( 'file:///x.svg#p' )\" width=\"5\" height=\"5\"/>",
            "<rect style=\"fill:&#x75;rl(x.svg#p)\" width=\"5\" height=\"5\"/>",
            "<style>rect { fill: \\75 rl(x.svg#p) }</style>",
            "<style>@import \"x.css\";</style>",
            "<g xml:base=\"file:///etc/\"><use href=\"#a\"/></g>",
            "<use href=\"other.svg#icon\"/>",
            "<script src=\"x.js\"/>",
            $"<g xmlns:q=\"http://www.w3.org/1999/xlink\"><image q:href=\"{outside}\"/></g>",
            $"<filter id=\"f\"><feImage href=\"{outside}\"/></filter>",
            $"<foreignObject width=\"5\" height=\"5\"><img xmlns=\"http://www.w3.org/1999/xhtml\" src=\"{outside}\"/></foreignObject>",
        ];
        string[] prologues =
        [
            "<?xml version=\"1.0\"?><!DOCTYPE svg [<!ENTITY e SYSTEM \"file:///etc/hosts\">]>",
            "<!DOCTYPE svg SYSTEM \"file:///etc/x.dtd\">",
            "<!DOCTYPE svg PUBLIC \"-//W3C//DTD SVG 1.1//EN\" \"http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd\">",
            "<?xml version=\"1.0\" encoding=\"UTF-7\"?>",
            "<?xml-stylesheet href=\"file:///etc/x.css\"?>",
        ];
        foreach (var document in external.Select(e => head + e + "</svg>").Concat(prologues.Select(p => p + head + "</svg>")))
            Check(FilePreviewClassifier.SvgLoadsExternalContent(Encoding.UTF8.GetBytes(document)), "external: " + document);
        Check(FilePreviewClassifier.SvgLoadsExternalContent([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes(head + $"<image href=\"{outside}\"/></svg>")]), "UTF-16 hides nothing");
        Check(FilePreviewClassifier.SvgLoadsExternalContent([0x3C, 0x00, 0x73, 0x00, 0x76, 0x00, 0x67, 0x00]), "undecodable bytes count as external");
        string[] local =
        [
            "<defs><linearGradient id=\"g\"/></defs><rect fill=\"url(#g)\" width=\"5\" height=\"5\"/><use xlink:href=\"#g\"/>",
            "<image href=\"data:image/png;base64,iVBORw0KGgo=\" width=\"1\" height=\"1\"/>",
            "<rect style=\"fill: url( '#g' )\" width=\"5\" height=\"5\"/><text>src and href are just words &amp; &#160;</text>",
        ];
        foreach (var document in local.Select(l => "<?xml version=\"1.0\" encoding=\"UTF-8\"?><!DOCTYPE svg>" + head + l + "</svg>"))
            Check(!FilePreviewClassifier.SvgLoadsExternalContent(Encoding.UTF8.GetBytes(document)), "local: " + document);
        return Task.CompletedTask;
    }

    internal static Task LinesBreakAndHighlightingMatchMacOS()
    {
        var scan = SourceLines.Scan("a\nbb\r\nccc\rd\u2028e\u2029f");
        Check(scan.Starts.SequenceEqual([0, 2, 6, 10, 12, 14]) && scan.Longest == 3, "line starts break like NSTextView");
        Check(SourceLines.Scan("").Starts.SequenceEqual([0]) && SourceLines.Scan("x\n").Starts.SequenceEqual([0, 2]) && SourceLines.Scan("\r\n\r\n").Starts.SequenceEqual([0, 2, 4]), "edge line starts");
        Check(SourceLines.Scan(new string('x', 6_000) + "\nshort").Longest == 6_000 && SourceLines.WrapThreshold == 5_000, "longest line and wrap threshold");

        List<(SourceTokenKind Kind, string Text)> Spans(string text, SourceLanguage language) => SourceHighlighter.Tokens(text, language).Select(t => (t.Kind, text.Substring(t.Location, t.Length))).ToList();
        var swift = Spans("let x = \"hi \\\"there\\\"\" // note\nreturn 42 /* a\nb */ letter", SourceLanguage.Swift);
        Check(swift.Select(s => s.Kind).SequenceEqual([SourceTokenKind.Keyword, SourceTokenKind.String, SourceTokenKind.Comment, SourceTokenKind.Keyword, SourceTokenKind.Number, SourceTokenKind.Comment]), "swift token kinds");
        Check(swift.Select(s => s.Text).SequenceEqual(["let", "\"hi \\\"there\\\"\"", "// note", "return", "42", "/* a\nb */"]), "swift token text");
        Check(Spans("def f():\n    \"\"\"doc\nmore\"\"\"\n    return x # done", SourceLanguage.Python).Select(s => s.Text).SequenceEqual(["def", "\"\"\"doc\nmore\"\"\"", "return", "# done"]), "python");
        Check(Spans("echo $# # count", SourceLanguage.Shell).Select(s => s.Text).SequenceEqual(["echo", "# count"]), "shell # needs a word boundary");
        Check(Spans("SELECT id FROM t -- all", SourceLanguage.Sql).Select(s => s.Text).SequenceEqual(["SELECT", "FROM", "-- all"]), "sql is case-insensitive");
        Check(Spans("{\"a\": true, \"b\": 1.5}", SourceLanguage.Json).Select(s => s.Kind).SequenceEqual([SourceTokenKind.String, SourceTokenKind.Keyword, SourceTokenKind.String, SourceTokenKind.Number]), "json literals");
        Check(Spans("\"open\nlet", SourceLanguage.Swift).Select(s => s.Text).SequenceEqual(["\"open", "let"]), "unterminated strings stop at the line end");
        Check(Spans("`multi\nline`", SourceLanguage.JavaScript).Select(s => s.Text).SequenceEqual(["`multi\nline`"]), "template strings span lines");
        Check(SourceHighlighter.Tokens("let x = 1", SourceLanguage.Plain).Count == 0, "plain text has no tokens");
        Check(Spans("<a href=\"x\"><!-- c --></a>", SourceLanguage.Html).Select(s => s.Kind).SequenceEqual([SourceTokenKind.String, SourceTokenKind.Comment]), "html");
        Check(SourceHighlighter.Tokens(string.Concat(Enumerable.Repeat("let ", 10)), SourceLanguage.Swift, maximumUnits: 8).Count == 2 && SourceHighlighter.MaximumUnits == 400_000, "scanning is capped");
        Check(!Spans("x1 = 2", SourceLanguage.Python).Any(s => s.Kind == SourceTokenKind.Number && s.Text == "1"), "digits inside a name are not numbers");
        return Task.CompletedTask;
    }

    internal static Task FilterSearchesOnlyOpenedFoldersAndCapsResults()
    {
        var tree = new FilePaneTree();
        tree.Apply("", new([new("src", "src", true), new("node_modules", "node_modules", true), new("App.swift", "App.swift", false)], false));
        tree.Apply("node_modules", new([new("app-lib", "node_modules/app-lib", true)], false));
        tree.Apply("src", new([new("app.ts", "src/app.ts", false), new("inner", "src/inner", true)], false));
        tree.Expanded.Add("src");
        var (rows, _) = tree.Rows();
        Check(rows.Select(r => (r.Entry.RelativePath, r.Depth)).SequenceEqual([("src", 0), ("src/app.ts", 1), ("src/inner", 1), ("node_modules", 0), ("App.swift", 0)]), "the tree shows opened folders indented");
        Check(!rows.First(r => r.Entry.Name == "node_modules").IsExpanded && rows.First(r => r.Entry.Name == "node_modules").Entry.IsNoise, "noise folders stay collapsed");
        tree.Filter = "  APP \n";
        var (found, cap) = tree.Rows();
        Check(found.Select(r => r.Entry.RelativePath).SequenceEqual(["App.swift", "src/app.ts"]) && !cap, "the trimmed filter searches the root and opened folders only, case-insensitively: " + string.Join(",", found.Select(r => r.Entry.RelativePath)));
        tree.Reveal("src/inner");
        Check(tree.Filter == "" && tree.Expanded.Contains("src") && tree.Expanded.Contains("src/inner") && tree.SelectedPath == "src/inner", "opening a filter result clears the filter and opens its folders");
        tree.Apply("src/inner", new([], false));
        Check(tree.Rows().Rows.First(r => r.Entry.RelativePath == "src/inner").Caption == Locale.Get("files.tree.empty"), "an empty opened folder says so");
        var many = new FilePaneTree();
        many.Apply("", new(Enumerable.Range(0, FilePaneTree.MaximumFilterResults + 5).Select(i => new WorkspaceFileEntry($"f{i}", $"f{i}", false)).ToList(), true));
        many.Filter = "f";
        Check(many.Rows().Rows.Count == 2_000 && many.Rows().HitCap, "the filter stops at 2,000 matches");
        Check(many.Caption("") == Locale.Get("files.tree.truncated", new Dictionary<string, string> { ["count"] = "5000" }), "a truncated folder says so");
        tree.Expanded.Remove("src/inner");
        Check(tree.RefreshTargets().SequenceEqual(["", "src"]) && !tree.Children.ContainsKey("node_modules"), "refresh rereads the root and opened folders and drops closed listings");
        return Task.CompletedTask;
    }

    internal static Task PaneOpensWithCtrlShiftEOnePerWorkspaceLeftAndIsNeverSaved()
    {
        Check(FilePaneKind.Shortcut == "Ctrl+Shift+E", "macOS ⇧⌘E maps to Ctrl+Shift+E");
        Check(FilePaneKind.PaneId("ws1") == "files:ws1" && Wire.Identifier(FilePaneKind.PaneId(Wire.Id())), "one pane id per workspace, a valid identifier");
        foreach (var language in new[] { "ko", "en" })
            foreach (var key in new[] { "menu.showFiles", "files.pane.title", "files.tree.filter", "files.tree.refresh", "files.preview.placeholder", "files.error.noRoom" })
                Check(Locale.Catalogue(language).ContainsKey(key), $"shared locale key {key} exists in {language}");

        var paneId = FilePaneKind.PaneId("ws1");
        var tree = PaneLayout.Normalize(new PaneLayoutNode { SessionIds = ["a", "b"], SelectedSessionId = "b" }, ["a", "b", paneId], "b")!;
        var placed = FilePaneKind.Place(tree, paneId, "b");
        Check(placed.Kind == "split" && placed.Axis == "horizontal" && placed.Children[0].SessionIds.SequenceEqual([paneId]) && placed.Children[1].SessionIds.SequenceEqual(["a", "b"]), "the pane opens left of the current pane's group");
        var lone = PaneLayout.Normalize(null, [paneId])!;
        Check(FilePaneKind.Place(lone, paneId, null) == lone, "alone it stays a tab");

        var root = Verification.Temp();
        var workspace = new Workspace { Id = "ws1", Name = "w", Path = root };
        var agent = new RunSession { Id = "agent1", WorkspaceId = "ws1", Title = "Claude" };
        var files = new RunSession { Id = paneId, WorkspaceId = "ws1", Title = Locale.Get("files.pane.title"), Kind = FilePaneKind.Kind, Draft = "never kept" };
        var stray = new RunSession { Id = "files-other", WorkspaceId = "ws1", Kind = FilePaneKind.Kind };
        var snapshot = new AppSnapshot { Workspaces = [workspace], Sessions = [agent, files, stray], ActiveWorkspaceId = "ws1", ActiveSessionId = paneId };
        var live = StateStore.Normalize(snapshot, false);
        Check(live.Sessions.Select(s => s.Id).SequenceEqual(["agent1", paneId]) && live.ActiveSessionId == paneId, "the running app keeps exactly one files pane per workspace");
        Check(live.Sessions.First(s => s.Id == paneId).Draft == "", "a files pane keeps no draft");
        Check(StateStore.Normalize(snapshot, true).Sessions.Select(s => s.Id).SequenceEqual(["agent1"]), "a restart never brings it back");
        var stored = FilePaneKind.Stored(live with { PaneLayoutActiveSessionIds = new() { ["ws1"] = paneId }, PaneLayouts = new() { ["ws1"] = placed } });
        Check(!System.Text.Json.JsonSerializer.Serialize(stored, Wire.Json).Contains(paneId, StringComparison.Ordinal), "the written state names the files pane nowhere");
        Check(stored.Sessions.Select(s => s.Id).SequenceEqual(["agent1"]), "it is never written to disk");
        Check(stored.ActiveSessionId is null && stored.PaneLayoutActiveSessionIds is { Count: 0 }, "no saved selection points at it");
        return Task.CompletedTask;
    }

    private static bool Throws<T>(Action action, Func<T, bool> matches) where T : Exception
    {
        try { action(); return false; }
        catch (T ex) { return matches(ex); }
    }

    internal static Task LinksToAnotherMachineAreRefusedOnPaper()
    {
        Check(WorkspaceFiles.UncShare(@"\\server\share\dir\a.png") == @"server\share", "plain UNC");
        Check(WorkspaceFiles.UncShare("//server/share/x") == @"server\share", "forward-slash UNC");
        Check(WorkspaceFiles.UncShare(@"\\?\UNC\server\share\x") == @"server\share" && WorkspaceFiles.UncShare(@"\\.\unc\Server\Share") == @"Server\Share", "device-path UNC");
        Check(WorkspaceFiles.UncShare(@"\\?\C:\Work") is null && WorkspaceFiles.UncShare(@"C:\Work\x") is null && WorkspaceFiles.UncShare("/Users/me/x") is null, "local paths are not UNC");
        Check(!WorkspaceFiles.FollowsLinkTarget(@"\\attacker\share\x", @"C:\Work\project\link"), "a local workspace link to a share is refused");
        Check(!WorkspaceFiles.FollowsLinkTarget(@"\\?\UNC\attacker\s", @"C:\Work\link") && !WorkspaceFiles.FollowsLinkTarget(@"\\", @"C:\Work\link"), "every UNC form is refused");
        Check(!WorkspaceFiles.FollowsLinkTarget(@"\\other\share\x", @"\\nas\team\project\link"), "a share workspace may not reach another share");
        Check(WorkspaceFiles.FollowsLinkTarget(@"\\NAS\Team\docs", @"\\nas\team\project\link"), "the same share is followed");
        Check(WorkspaceFiles.FollowsLinkTarget(@"C:\Work\other", @"C:\Work\link") && WorkspaceFiles.FollowsLinkTarget("/tmp/x", "/Users/me/link"), "local targets are followed");
        return Task.CompletedTask;
    }

    internal static Task SvgSizeIsReadFromItsRootElement()
    {
        static (double, double)? Size(string svg) => FilePreviewClassifier.SvgSize(System.Text.Encoding.UTF8.GetBytes(svg));
        Check(Size("<svg xmlns='http://www.w3.org/2000/svg' width=\"40\" height=\"30\"/>") == (40, 30), "plain numbers");
        Check(Size("<?xml version='1.0'?><svg width='72pt' height='1in'></svg>") == (96, 96), "absolute units become CSS pixels");
        Check(Size("<svg viewBox='0 0 120,80'/>") == (120, 80), "viewBox when there is no size");
        Check(Size("<svg width='50' viewBox='0 0 120 80'/>") == (50, 80), "a missing side comes from the viewBox");
        Check(Size("<svg width='100%' height='100%'/>") is null && Size("<g/>") is null && Size("<svg width='2em' height='3em'/>") is null, "relative or missing sizes are unknown");
        var huge = Size("<svg width='20000000' height='10'/>");
        Check(huge is { } h && !FilePreviewClassifier.IsDrawable(h.Item1, h.Item2), "a size past MaximumVectorPoints is not drawable");
        var infinite = Size("<svg width='1e400' height='1'/>");
        Check(infinite is { } i && !FilePreviewClassifier.IsDrawable(i.Item1, i.Item2), "an overflowing size is not drawable");
        Check(Size("<svg width='40' height='30'/>") is { } ok && FilePreviewClassifier.IsDrawable(ok.Item1, ok.Item2), "an ordinary svg is drawable");
        return Task.CompletedTask;
    }

    internal static Task TheWindowsPaneDrawsAtMostTwoThousandRowsAnd256KB()
    {
        var rows = Enumerable.Range(0, 2_500).ToList();
        var (drawn, capped) = FilePaneDrawing.TreeRows(rows);
        Check(capped && drawn.Count == FilePaneDrawing.MaximumTreeRows && drawn[^1] == 1_999, "2,000 rows drawn, the rest noted");
        var (all, none) = FilePaneDrawing.TreeRows(rows.Take(2_000).ToList());
        Check(!none && all.Count == 2_000, "exactly the cap is not cut");
        Check(FilePaneDrawing.SourceText("short\ntext") == ("short\ntext", false), "a short file is drawn whole");
        var line = new string('a', 99) + "\r\n";
        var text = string.Concat(Enumerable.Repeat(line, 3_000));
        var (cut, cutCapped) = FilePaneDrawing.SourceText(text);
        Check(cutCapped && cut.Length <= FilePaneDrawing.MaximumSourceUnits && cut.EndsWith('a') && text.StartsWith(cut, StringComparison.Ordinal), "cut at a line break, no stray CR");
        var oneLine = new string('b', FilePaneDrawing.MaximumSourceUnits - 1) + "😀" + "tail";
        var (single, singleCapped) = FilePaneDrawing.SourceText(oneLine);
        Check(singleCapped && single.Length == FilePaneDrawing.MaximumSourceUnits - 1 && !char.IsHighSurrogate(single[^1]), "one long line is cut at the cap, never inside a surrogate pair");
        return Task.CompletedTask;
    }
}
