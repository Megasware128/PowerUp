using System;
using System.Linq;
using Newtonsoft.Json.Linq;
using Nuke.Common;
using Nuke.Common.CI;
using Nuke.Common.Execution;
using Nuke.Common.Git;
using Nuke.Common.IO;
using Nuke.Common.ProjectModel;
using Nuke.Common.Tooling;
using Nuke.Common.Tools.DotNet;
using Nuke.Common.Tools.GitVersion;
using Nuke.Common.Utilities.Collections;
using static Nuke.Common.EnvironmentInfo;
using static Nuke.Common.IO.FileSystemTasks;
using static Nuke.Common.IO.PathConstruction;
using static Nuke.Common.Tools.DotNet.DotNetTasks;
using static Nuke.Common.Tools.Git.GitTasks;

[CheckBuildProjectConfigurations]
[ShutdownDotNetAfterServerBuild]
class Build : NukeBuild
{
    /// Support plugins are available for:
    ///   - JetBrains ReSharper        https://nuke.build/resharper
    ///   - JetBrains Rider            https://nuke.build/rider
    ///   - Microsoft VisualStudio     https://nuke.build/visualstudio
    ///   - Microsoft VSCode           https://nuke.build/vscode

    public static int Main () => Execute<Build>(x => x.Compile);

    [Parameter("Configuration to build - Default is 'Debug' (local) or 'Release' (server)")]
    readonly Configuration Configuration = IsLocalBuild ? Configuration.Debug : Configuration.Release;

    [Solution(GenerateProjects = true)] readonly Solution Solution;
    [GitRepository] readonly GitRepository GitRepository;
    [GitVersion] readonly GitVersion GitVersion;

    AbsolutePath SourceDirectory => RootDirectory / "src";
    AbsolutePath OutputDirectory => RootDirectory / "output";

    Target Clean => _ => _
        .Before(Restore)
        .Executes(() =>
        {
            SourceDirectory.GlobDirectories("**/bin", "**/obj").ForEach(DeleteDirectory);
            EnsureCleanDirectory(OutputDirectory);
        });

    Target Restore => _ => _
        .Executes(() =>
        {
            DotNetRestore(s => s
                .SetProjectFile(Solution));
        });

    Target Compile => _ => _
        .DependsOn(Restore)
        .Executes(() =>
        {
            DotNetBuild(s => s
                .SetProjectFile(Solution)
                .SetConfiguration(Configuration)
                .SetAssemblyVersion(GitVersion.AssemblySemVer)
                .SetFileVersion(GitVersion.AssemblySemFileVer)
                .SetInformationalVersion(GitVersion.InformationalVersion)
                .EnableNoRestore());
        });

    Target Pack => _ => _
        .DependsOn(Compile)
        .Executes(() =>
        {
            DotNetPack(s => s
                .SetProject(Solution.PowerUp_Watcher)
                .SetConfiguration(Configuration)
                .EnableNoBuild()
                .EnableNoRestore()
                .SetOutputDirectory(OutputDirectory));
        });

    Target Install => _ => _
        .DependsOn(Pack)
        .Executes(() =>
        {
            DotNetToolInstall(s => s
                .SetPackageName("PowerUp.Watcher")
                .EnableGlobal()
                .AddSources(OutputDirectory));
        });

    Target Update => _ => _
        .DependsOn(Pack)
        .Executes(() =>
        {
            DotNetToolUpdate(s => s
                .SetPackageName("PowerUp.Watcher")
                .EnableGlobal()
                .AddSources(OutputDirectory));
        });

    Target SetDotNetCorePaths => _ => _
        .Executes(() =>
        {
            var dotnetPaths = DotNet("--list-runtimes").Select(x => x.Text)
                                                       .Where(x => x.StartsWith("Microsoft.NETCore.App"))
                                                       .Select(x => x.Split(' '))
                                                       .Select(x => (version: x[1], path: (AbsolutePath)(string.Concat(x[2..])[1..^1])))
                                                       .ToList();

            var dotnetPathDir50 = dotnetPaths.LastOrDefault(x => x.version.StartsWith("5.0."));
            var dotnetPathDir60 = dotnetPaths.LastOrDefault(x => x.version.StartsWith("6.0."));

            var dotnetPathDirLatest = dotnetPaths.LastOrDefault();

            var appsettings = SerializationTasks.JsonDeserializeFromFile<JObject>(Solution.PowerUp_Watcher.Directory / "appsettings.json");

            if (dotnetPathDir50 != default) appsettings["DotNetCoreDirPathNet5"] = (dotnetPathDir50.path / dotnetPathDir50.version).ToString();
            if (dotnetPathDir60 != default) appsettings["DotNetCoreDirPathNet6"] = (dotnetPathDir60.path / dotnetPathDir60.version).ToString();
            if (dotnetPathDirLatest != default) appsettings["DotNetCoreDirPathDefault"] = (dotnetPathDirLatest.path / dotnetPathDirLatest.version).ToString();

            SerializationTasks.JsonSerializeToFile(appsettings, Solution.PowerUp_Watcher.Directory / "appsettings.json");
        })
        .DependentFor(Pack);

    Target Pull => _ => _
        .DependentFor(Update)
        .Before(Restore, SetDotNetCorePaths)
        .Executes(() =>
        {
            if (GitRepository.IsOnMainBranch())
            {
                Git("pull");
            }
            else
            {
                Git("checkout " + Solution.PowerUp_Watcher.Directory / "appsettings.json");

                var currentBranch = GitRepository.Branch;

                Git("checkout main");
                if (IsFork)
                {
                    Git("pull --set-upstream upstream main");
                }
                else
                {
                    Git("pull");
                }
                Git("checkout " + currentBranch);
                Git("merge main");
            }
        });

    bool IsFork => Git("remote").Any(x => x.Text == "upstream");
}
