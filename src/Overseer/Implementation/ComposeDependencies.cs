using Overseer.GitHub;
using Overseer.Sandboxes;

namespace Overseer.Implementation;

/// <summary>
/// The services of the repositories' Compose files in a Sandbox, each repository's started in a project no other repository's
/// services are in (<see cref="ComposeProjects"/>).
/// </summary>
public sealed class ComposeDependencies(RepositoryCommands commands)
{
    /// <summary>For starting a repository's Compose services, with pulling and building their images.</summary>
    private static readonly TimeSpan StartLimit = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long Compose itself waits for the services to run and be healthy: it then says which one isn't, which is worth more
    /// than giving up on the command.
    /// </summary>
    private static readonly TimeSpan WaitLimit = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Removes the containers of every Compose project started from a file in the clone, whatever the project is named, leaving
    /// volumes: the repository's services may have run under another name than they start under now, and would hold their ports.
    /// </summary>
    private const string RemoveContainers =
        """
        docker ps --all --format '{{.ID}} {{.Label "com.docker.compose.project.working_dir"}}' |
          awk -v clone="$PWD" '$2 == clone || index($2, clone "/") == 1 { print $1 }' |
          xargs -r docker rm --force > /dev/null || exit
        """;

    /// <summary>Starts services, and waits for them to run, stopping at the first command that fails.</summary>
    /// <param name="composed">Every repository with a Compose file, with that file's path from the repository's root.</param>
    /// <param name="starting">The ones of <paramref name="composed"/> whose services to start.</param>
    /// <param name="replacing">Whether services of theirs may run already, which are then replaced.</param>
    /// <param name="entering">Called before each command, with the repository it is about.</param>
    /// <returns>The commands run, in order. Only the last one can have failed, and then nothing was run after it.</returns>
    public async Task<IReadOnlyList<ComposeCommand>> StartAsync(
        Sandbox sandbox, IReadOnlyList<(RepositoryName Repository, string File)> composed,
        IReadOnlyCollection<RepositoryName> starting, bool replacing, Func<RepositoryName, Task> entering,
        CancellationToken cancellationToken)
    {
        var run = new List<ComposeCommand>();
        // Compose is asked each project's name before any starts. One repository alone shares its name with none.
        var names = new List<(RepositoryName Repository, string Name)>();
        foreach (var (repository, file) in composed.Count > 1 && starting.Count > 0 ? composed : [])
        {
            await entering(repository);
            var what = $"Reading the Compose file of {repository}";
            var read = await commands.RunAsync(sandbox, repository, ProjectConfig(file), RepositoryCommands.GitLimit, cancellationToken);
            if (read is CommandRun.Succeeded succeeded && ComposeProjects.NameIn(succeeded.Output) is { } name)
            {
                names.Add((repository, name));
                continue;
            }
            run.Add(new ComposeCommand(
                repository, what, ConfigCommand(file), read,
                read is CommandRun.Succeeded ? $"{what} failed: Compose gave no project name for it." : null));
            return run;
        }

        var renamed = ComposeProjects.Renamed(names);
        foreach (var (repository, file) in composed.Where(project => starting.Contains(project.Repository)))
        {
            await entering(repository);
            var project = renamed.TryGetValue(repository, out var own) ? $" --project-name {ShellText.Quoted(own)}" : "";
            // --wait returns only once every service runs, and is healthy where it has a health check.
            var up = $"docker compose --file {ShellText.Quoted(file)}{project} up --detach --wait --wait-timeout {WaitLimit.TotalSeconds:0}";
            var started = await commands.RunAsync(
                sandbox, repository, replacing ? $"{RemoveContainers}\n{up}" : up, StartLimit, cancellationToken);
            run.Add(new ComposeCommand(repository, $"Starting the Compose services of {repository}", up, started, Problem: null));
            if (started is not CommandRun.Succeeded)
            {
                break;
            }
        }
        return run;
    }

    /// <summary>
    /// Prints a Compose file's project as Compose reads it, as JSON, and nothing else when it can: warnings, which Compose writes to
    /// standard error, are printed only when it fails.
    /// </summary>
    private static string ProjectConfig(string file) => ShellText.OutputAlone(ConfigCommand(file));

    private static string ConfigCommand(string file) => $"docker compose --file {ShellText.Quoted(file)} config --format json";
}

/// <summary>A command run to start Compose services.</summary>
/// <param name="What">What it does, to say how it went wrong.</param>
/// <param name="Command">The Compose command.</param>
/// <param name="Problem">Why it didn't do its job although it succeeded; <c>null</c> otherwise.</param>
public sealed record ComposeCommand(RepositoryName Repository, string What, string Command, CommandRun Run, string? Problem)
{
    /// <summary>A sentence saying how it went wrong, without what it printed; <c>null</c> when it did its job.</summary>
    public string? Failure => Problem ?? Run.FailureOf(What);

    /// <summary>As <see cref="Failure"/>, with the end of what a command that failed printed, to show the human.</summary>
    public string? Error => Problem ?? Run.ErrorOf(What);
}
