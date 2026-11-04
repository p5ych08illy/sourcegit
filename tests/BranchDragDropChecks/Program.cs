using System.Diagnostics;
using Avalonia;
using Avalonia.Threading;
using SourceGit;
using SourceGit.ViewModels;

internal static class Program
{
    private static int _checks;

    [STAThread]
    private static void Main()
    {
        var fixture = Path.Combine(Path.GetTempPath(), "sourcegit-branch-drag-drop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        SourceGit.Native.OS.BasicDirectories.ConfigDir = fixture;
        SourceGit.Native.OS.BasicDirectories.CacheDir = fixture;
        try
        {
            App.BuildAvaloniaApp().SetupWithoutStarting();
            App.SetLocale("en_US");
            CheckDropTargets();
            CheckOverlappingSources(fixture);
            CheckFolderMove(fixture);
            CheckRootMove(fixture);
            CheckStaleDestination(fixture);
            CheckDestinationConflicts(fixture);
            Console.WriteLine($"PASS: {_checks} branch drag-and-drop checks");
        }
        finally
        {
            var absolute = Path.GetFullPath(fixture);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!absolute.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Fixture must stay inside the temporary directory");
            for (var attempt = 0; attempt < 20; attempt++)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(absolute, "*", SearchOption.AllDirectories))
                        File.SetAttributes(file, FileAttributes.Normal);
                    Directory.Delete(absolute, true);
                    break;
                }
                catch (IOException) when (attempt < 19) { Thread.Sleep(50); }
                catch (UnauthorizedAccessException) when (attempt < 19) { Thread.Sleep(50); }
            }
        }
    }

    private static void CheckDropTargets()
    {
        Check(!MoveBranches.CanDropOnto([], ""), "an empty drag cannot be dropped");
        Check(MoveBranches.CanDropOnto(["refs/heads/topic"], "feature"), "a local branch can move into a folder");
        Check(MoveBranches.CanDropOnto(["refs/heads/feature/topic"], ""), "a nested local branch can move to the root");
        Check(!MoveBranches.CanDropOnto(["refs/heads/topic"], ""), "a root branch dropped onto root is a no-op");
        Check(!MoveBranches.CanDropOnto(["refs/heads/feature/topic"], "feature"), "a branch dropped onto its parent is a no-op");
        Check(!MoveBranches.CanDropOnto(["refs/heads/feature"], "feature"), "a folder cannot move into itself");
        Check(!MoveBranches.CanDropOnto(["refs/heads/feature"], "feature/nested"), "a folder cannot move into its descendant");
        Check(MoveBranches.CanDropOnto(["refs/heads/topic", "refs/heads/feature/other"], ""), "a selection can include root no-ops when another branch moves");
        Check(!MoveBranches.CanDropOnto(["refs/heads/feature", "refs/heads/other"], "feature/nested"), "one invalid selected path rejects the whole drop");
        Check(!MoveBranches.CanDropOnto(["refs/remotes/origin/topic"], "feature"), "remote branches cannot be moved into local folders");
        Check(!MoveBranches.CanDropOnto(["refs/heads/topic", "refs/remotes/origin/other"], "feature"), "mixed local and remote selections cannot be moved");
    }

    private static void CheckOverlappingSources(string root)
    {
        var fixture = new GitFixture(root, "overlapping");
        fixture.Branch("x/y", fixture.First);
        fixture.Branch("dest/x/y", fixture.Second);
        var repo = fixture.Load();
        var popup = new MoveBranches(repo, ["refs/heads/x", "refs/heads/dest/x/y"], "dest");
        Check(popup.Preview.SequenceEqual(new[] { "x/y  →  dest/x/y", "dest/x/y  →  dest/y" }), "the overlapping-source regression exercises the original overwrite plan");
        Check(popup.HasError, "a destination that is another selected source is rejected");
        Check(!Complete(popup.Sure()), "a conflicting move cannot be confirmed");
        Check(fixture.Ref("x/y") == fixture.First && fixture.Ref("dest/x/y") == fixture.Second, "rejecting overlapping moves preserves both original branch tips");
        Check(!fixture.HasRef("dest/y"), "rejecting overlapping moves does not partially rename branches");
        popup.Cleanup();
    }

    private static void CheckFolderMove(string root)
    {
        var fixture = new GitFixture(root, "folder");
        fixture.Branch("x/one", fixture.First);
        fixture.Branch("x/nested/two", fixture.Second);
        fixture.Branch("dest/keep", fixture.Second);
        Git(fixture.Path, "checkout", "x/one");
        var repo = fixture.Load();
        var popup = new MoveBranches(repo, ["refs/heads/x"], "dest");
        Check(!popup.HasError && popup.Preview.Count == 2, "a folder move previews all descendant branches");
        Check(Complete(popup.Sure()), "a normal folder move succeeds");
        Check(fixture.Ref("dest/x/one") == fixture.First && fixture.Ref("dest/x/nested/two") == fixture.Second, "folder moves preserve each branch tip and relative hierarchy");
        Check(!fixture.HasRef("x/one") && !fixture.HasRef("x/nested/two"), "folder moves remove the old branch names");
        Check(fixture.Ref("dest/keep") == fixture.Second, "folder moves preserve unrelated destination branches");
        Check(Git(fixture.Path, "symbolic-ref", "HEAD").Trim() == "refs/heads/dest/x/one", "moving the checked-out branch preserves its checkout");
        Check(repo.CurrentBranch?.FullName == "refs/heads/dest/x/one" && repo.Branches.Any(x => x.Name == "dest/x/nested/two"), "successful moves refresh repository branch models");
        popup.Cleanup();
    }

    private static void CheckRootMove(string root)
    {
        var fixture = new GitFixture(root, "root");
        fixture.Branch("feature/topic", fixture.First);
        var repo = fixture.Load();
        var popup = new MoveBranches(repo, ["refs/heads/feature/topic"], "");
        Check(!popup.HasError && Complete(popup.Sure()), "a nested branch can be moved to the root");
        Check(fixture.Ref("topic") == fixture.First && !fixture.HasRef("feature/topic"), "a root move strips only the selected branch's parent");
        popup.Cleanup();
    }

    private static void CheckStaleDestination(string root)
    {
        var fixture = new GitFixture(root, "stale");
        fixture.Branch("x/y", fixture.First);
        var repo = fixture.Load();
        var popup = new MoveBranches(repo, ["refs/heads/x/y"], "dest");
        Check(!popup.HasError, "the stale-destination fixture initially has a valid move");
        fixture.Branch("dest/y", fixture.Second);
        Check(!repo.Branches.Any(x => x.Name == "dest/y"), "the popup model predates the externally created destination");
        Check(!Complete(popup.Sure()), "Git rejects a destination created after the move preview");
        Check(fixture.Ref("dest/y") == fixture.Second, "a stale move never overwrites the new destination tip");
        Check(fixture.Ref("x/y") == fixture.First, "a rejected stale move retains its source branch");
        popup.Cleanup();
    }

    private static void CheckDestinationConflicts(string root)
    {
        var fixture = new GitFixture(root, "conflicts");
        fixture.Branch("source/topic", fixture.First);
        fixture.Branch("dest/topic/child", fixture.Second);
        fixture.Branch("other/nested/leaf", fixture.First);
        fixture.Branch("dest/other/nested", fixture.Second);
        var repo = fixture.Load();
        var parentConflict = new MoveBranches(repo, ["refs/heads/source/topic"], "dest");
        Check(parentConflict.HasError, "a destination cannot replace the folder of an existing branch");
        var childConflict = new MoveBranches(repo, ["refs/heads/other"], "dest");
        Check(childConflict.HasError, "a destination cannot be nested under an existing branch");
        Check(fixture.Ref("source/topic") == fixture.First && fixture.Ref("dest/topic/child") == fixture.Second, "prefix validation preserves existing branches");
        parentConflict.Cleanup();
        childConflict.Cleanup();
    }

    private static T Complete<T>(Task<T> task)
    {
        var timer = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            if (timer.Elapsed.TotalSeconds > 15)
                throw new TimeoutException("Timed out waiting for branch operation");
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
        return task.GetAwaiter().GetResult();
    }

    private static void Check(bool condition, string name)
    {
        if (!condition)
            throw new Exception("FAIL: " + name);
        _checks++;
    }

    private static string Git(string path, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = path,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
            start.ArgumentList.Add(arg);
        using var process = Process.Start(start);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            throw new TimeoutException("Timed out running fixture Git command");
        }
        if (process.ExitCode != 0)
            throw new Exception(error.GetAwaiter().GetResult());
        return output.GetAwaiter().GetResult();
    }

    private sealed class GitFixture
    {
        public string Path { get; }
        public string First { get; }
        public string Second { get; }

        public GitFixture(string root, string name)
        {
            Path = System.IO.Path.Combine(root, name);
            Directory.CreateDirectory(Path);
            Git(Path, "init", "-b", "main");
            Git(Path, "config", "user.name", "Test");
            Git(Path, "config", "user.email", "test@example.com");
            Git(Path, "config", "commit.gpgsign", "false");
            Git(Path, "config", "core.hooksPath", ".git/disabled-hooks");
            Git(Path, "commit", "--allow-empty", "-m", "first");
            First = Ref("main");
            Git(Path, "commit", "--allow-empty", "-m", "second");
            Second = Ref("main");
        }

        public void Branch(string name, string revision) => Git(Path, "branch", name, revision);

        public string Ref(string name) => Git(Path, "rev-parse", "refs/heads/" + name).Trim();

        public bool HasRef(string name) => Git(Path, "for-each-ref", "--format=%(refname)", "refs/heads/" + name)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Contains("refs/heads/" + name);

        public Repository Load()
        {
            // Do not open watchers: the stale-preview regression deliberately keeps the model unchanged.
            var repo = new Repository(false, Path, System.IO.Path.Combine(Path, ".git"));
            repo.Branches.AddRange(Complete(new SourceGit.Commands.QueryBranches(Path).GetResultAsync()));
            Check(repo.Branches.Any(x => x.Name == "main"), "fixture branches load through the production Git query");
            return repo;
        }
    }
}
