using System.Diagnostics;
using System.Text.Json;
using Teleporter.Core;
using Teleporter.Sessions;
using Teleporter.Transfer;
using Xunit;

namespace Teleporter.Tests;

public class PathMapTests
{
    [Theory]
    [InlineData(@"C:\Users\me\Proj", @"C:\Users\me\Proj", "/home/dev/projects/Proj", "/home/dev/projects/Proj")]
    [InlineData(@"c:\users\me\proj\Sub\Deep", @"C:\Users\me\Proj", "/home/dev/projects/Proj", "/home/dev/projects/Proj/Sub/Deep")]
    [InlineData("/c/Users/me/Proj/src", @"C:\Users\me\Proj", "/home/dev/projects/Proj", "/home/dev/projects/Proj/src")]
    [InlineData("/home/dev/projects/Proj/a b", "/home/dev/projects/Proj", @"C:\Work\Proj", @"C:\Work\Proj\a b")]
    [InlineData("/home/dev/projects/Proj", "/home/dev/projects/Proj/", @"C:\Work\Proj\", @"C:\Work\Proj")]
    public void MapsInsideTheRoot(string value, string oldRoot, string newRoot, string expected) =>
        Assert.Equal(expected, PathMap.MapCwd(value, oldRoot, newRoot));

    [Theory]
    [InlineData(@"C:\Users\me\Project2", @"C:\Users\me\Proj")]   // prefix of the name is not inside
    [InlineData(@"C:\elsewhere", @"C:\Users\me\Proj")]
    [InlineData("/home/dev/projects/proj", "/home/dev/projects/Proj")] // Linux is case-sensitive
    public void LeavesOutsidePathsAlone(string value, string oldRoot) =>
        Assert.Null(PathMap.MapCwd(value, oldRoot, "/x"));

    [Theory]
    [InlineData(@"C:\Users\me\Documents\My Project", "c--Users-me-Documents-My-Project")]
    [InlineData("/home/dev/projects/my.app_1", "-home-dev-projects-my-app-1")]
    public void EncodesClaudeFolderNames(string path, string expected) =>
        Assert.Equal(expected, PathMap.ClaudeFolderName(path));

    [Fact]
    public void RefusesFolderNamesClaudeWouldHash() =>
        Assert.Throws<InvalidOperationException>(() => PathMap.ClaudeFolderName("/" + new string('a', 250)));
}

public class NameChecksTests
{
    [Theory]
    [InlineData("a:b.txt")]
    [InlineData("what?")]
    [InlineData("trailing.")]
    [InlineData("trailing ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("com1.log")]
    public void RejectsInvalidWindowsNames(string name) => Assert.NotNull(NameChecks.SegmentProblem(name));

    [Theory]
    [InlineData("README.md")]
    [InlineData(".gitignore")]
    [InlineData("console.log")]
    [InlineData("unicodé-ñame.txt")]
    public void AcceptsValidNames(string name) => Assert.Null(NameChecks.SegmentProblem(name));

    [Fact]
    public void FindsCaseCollisions()
    {
        var problems = NameChecks.WindowsProblems(new[] { "src/Readme.md", "src/README.md", "src/other.md" });
        Assert.Single(problems);
        Assert.Contains("letter case", problems[0]);
    }

    [Fact]
    public void WarnsAboutLongPaths()
    {
        var (problems, warnings) = NameChecks.Check(new[] { new string('a', 120) + "/" + new string('b', 120) }, @"C:\Users\me\projects\x");
        Assert.Empty(problems);
        Assert.Single(warnings);
    }
}

public class ManifestTests
{
    private static Manifest Make(params (string path, long size, string hash)[] files)
    {
        var m = new Manifest();
        foreach (var f in files) m.Files[f.path] = new ManifestEntry(f.path, f.size, f.hash);
        return m;
    }

    [Fact]
    public void IdenticalManifestsMatch() =>
        Assert.Empty(Manifest.Compare(Make(("a", 1, "x"), ("b/c", 2, "y")), Make(("b/c", 2, "Y"), ("a", 1, "x"))));

    [Fact]
    public void ReportsEveryKindOfDifference()
    {
        var diff = Manifest.Compare(Make(("a", 1, "x"), ("b", 2, "y"), ("c", 3, "z")), Make(("a", 1, "q"), ("b", 5, "y"), ("d", 1, "w")));
        Assert.Contains(diff, d => d.StartsWith("content differs: a"));
        Assert.Contains(diff, d => d.StartsWith("size differs: b"));
        Assert.Contains(diff, d => d.StartsWith("missing: c"));
        Assert.Contains(diff, d => d.StartsWith("unexpected extra file: d"));
    }

    [Fact]
    public void EmptyFoldersMustArrive()
    {
        var src = Make(("a", 1, "x"));
        src.EmptyDirs.Add("empty");
        Assert.Contains("missing empty folder: empty", Manifest.Compare(src, Make(("a", 1, "x"))));
    }

    [Fact]
    public void LocalScanSkipsNamedFoldersAndHashes()
    {
        string root = Directory.CreateTempSubdirectory("tp-test-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "a.txt"), "hello");
            Directory.CreateDirectory(Path.Combine(root, "node_modules", "x"));
            File.WriteAllText(Path.Combine(root, "node_modules", "x", "i.js"), "1");
            Directory.CreateDirectory(Path.Combine(root, "empty"));
            var m = Manifest.BuildLocal(root, new HashSet<string> { "node_modules" }, null, CancellationToken.None);
            Assert.Equal(new[] { "a.txt" }, m.Files.Keys);
            Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", m.Files["a.txt"].Sha256);
            Assert.Contains("empty", m.EmptyDirs);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class SessionRewriteTests
{
    [Fact]
    public void RewritesOnlyTheTopLevelCwd()
    {
        string line = JsonSerializer.Serialize(new { type = "user", cwd = @"C:\P", message = new { cwd = @"C:\P" } });
        string result = ClaudeSessions.RewriteLine(line, @"C:\P", "/home/dev/projects/P");
        using var doc = JsonDocument.Parse(result);
        Assert.Equal("/home/dev/projects/P", doc.RootElement.GetProperty("cwd").GetString());
        Assert.Equal(@"C:\P", doc.RootElement.GetProperty("message").GetProperty("cwd").GetString());
    }

    [Theory]
    [InlineData("not json at all, \"cwd\"")]
    [InlineData("{\"type\":\"x\"}")]
    [InlineData("{\"cwd\":\"/somewhere/else\"}")]
    public void KeepsOtherLinesByteForByte(string line) =>
        Assert.Equal(line, ClaudeSessions.RewriteLine(line, @"C:\P", "/x"));
}

/// <summary>helper.py does the same path mapping on the server; both must agree.</summary>
public class HelperParityTests
{
    [Theory]
    [InlineData(@"c:\users\me\proj\Sub", @"C:\Users\me\Proj", "/home/dev/projects/Proj")]
    [InlineData("/c/Users/me/Proj/src", @"C:\Users\me\Proj", "/home/dev/projects/Proj")]
    [InlineData("/home/dev/projects/Proj/a b", "/home/dev/projects/Proj", @"C:\Work\Proj")]
    [InlineData(@"C:\elsewhere", @"C:\Users\me\Proj", "/x")]
    public void PythonAndCSharpMapTheSame(string value, string oldRoot, string newRoot)
    {
        string? python = FindPython();
        if (python is null) return; // not installed here; covered on machines that have it
        string helper = Path.Combine(AppContext.BaseDirectory, "helper.py");
        string code = "import importlib.util,json,sys,re;" +
                      $"s=importlib.util.spec_from_file_location('h',r'{helper}');h=importlib.util.module_from_spec(s);s.loader.exec_module(h);" +
                      "a=json.loads(sys.stdin.read());r=h.map_cwd(a[0],a[1],a[2],bool(re.match(r'^[a-zA-Z]:',a[2])));print(json.dumps(r))";
        var psi = new ProcessStartInfo(python, new[] { "-c", code }) { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        using var p = Process.Start(psi)!;
        p.StandardInput.Write(JsonSerializer.Serialize(new[] { value, oldRoot, newRoot }));
        p.StandardInput.Close();
        string? fromPython = JsonSerializer.Deserialize<string?>(p.StandardOutput.ReadToEnd());
        p.WaitForExit();
        Assert.Equal(PathMap.MapCwd(value, oldRoot, newRoot), fromPython);
    }

    private static string? FindPython()
    {
        foreach (string candidate in new[] { "py", "python3", "python" })
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo(candidate, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
                p!.WaitForExit(5000);
                if (p.ExitCode == 0) return candidate;
            }
            catch { }
        }
        return null;
    }
}
