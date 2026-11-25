using Overseer.Intents;
using Overseer.Repositories;
using Overseer.Sandboxes;
using Overseer.Specs;

namespace Overseer.Implementation;

/// <summary>What the Designer or a Developer is told when its session on a Task starts.</summary>
internal static class TaskInstructions
{
    /// <summary>
    /// The first message of a Task's session: the instructions of the Task's role, the Intent's Approved Spec, and the Task the
    /// session is for, with the repositories it touches; and, when the Task's last verification failed, how.
    /// </summary>
    /// <param name="task">A Task of the Intent's own Task list, which may differ from the Approved Spec's.</param>
    /// <param name="taskCount">How many Tasks the Intent has.</param>
    /// <param name="failedLast">
    /// The step that failed the Task's last verification attempt, which an earlier session made; <c>null</c> when none was made, or
    /// it passed.
    /// </param>
    public static string FirstMessage(Intent intent, Spec approved, IntentTask task, int taskCount, VerificationStep? failedLast) =>
        $"""
        {RoleOf(task.Role)}

        The Intent's Repositories in scope, the GitHub repositories it may change, are cloned in this Sandbox, each on the Intent
        branch, `{intent.Branch}`:

        {string.Join("\n", intent.RepositoriesInScope.Select(repository => $"- {repository}, in {Sandbox.CloneDirectory(repository)}"))}

        In each that has a Repository Manifest, `{RepositoryManifest.Path}` at its root, the `setup` command has been run and the
        services of its Compose file are running. The manifest also holds the commands that build it (`build`) and test it (`test`).

        The Intent's Tasks are carried out one at a time, in order, each by an agent of its own. Do your Task, and leave the others to
        theirs. The repositories hold the work of the Tasks before yours. An earlier session on your Task, one that broke off or whose
        work failed verification, may have left work there too: look with `git status` and `git log`, and go on from it.

        How to work:

        - Read the agent guidance of each repository you change, such as its `AGENTS.md`, and follow it.
        - Before you end your turn, run the `build` and `test` commands of each repository you changed, and make them pass.
        - When you end your turn, Overseer verifies your work itself: it runs those commands in each repository your Task touches,
          and in each other one you changed, after `setup` where you changed the manifest or the Compose file. What fails comes back
          to you to fix. What passes, Overseer commits where you haven't, and pushes: leave nothing behind that doesn't belong in
          the repository, such as build outputs that its `.gitignore` doesn't cover.
        - Leave pushing to Overseer: never run `git push`, don't open or change pull requests, and don't change anything else on
          GitHub. Stay on the Intent branch.
        - No one can answer questions during a Task. Where the Spec and your Task leave something open, decide for yourself.
        - Don't end your turn with a question, nor with an offer such as "Shall I go on?": Overseer takes a last message that ends
          in a question mark as a question, which stops the implementation. End your turn with a short statement of what you did and
          what you decided.

        # The Approved Spec

        The human approved this Spec. It is what the work is held against.

        {SpecText.Of(approved)}

        # Your Task

        Your Task is Task {task.Number} of {taskCount}: {task.Title}

        Repositories it touches: {string.Join(", ", task.Repositories)}

        {task.Description}{(failedLast is null ? "" : $"\n\n{LastFailure(failedLast)}")}
        """;

    /// <summary>What a new session on a Task is told about the verification that failed before it.</summary>
    private static string LastFailure(VerificationStep failed) =>
        $"""
        # What failed last time

        The last time Overseer verified work on your Task, before your session, it failed. Your work has to pass where that didn't.

        {FailureOf(failed)}
        """;

    /// <summary>What a Task's session is told when its work failed verification, for it to fix.</summary>
    /// <param name="failed">The step of the verification that failed.</param>
    /// <param name="attempt">Which verification attempt that was, from 1, of the <paramref name="allowed"/> ones.</param>
    public static string VerificationFailed(IntentTask task, VerificationStep failed, int attempt, int allowed) =>
        $"""
        Overseer verified your work on Task {task.Number}, and it failed: attempt {attempt} of {allowed}.

        {FailureOf(failed)}

        Fix it, and end your turn as before. Overseer then verifies your work again.
        """;

    /// <summary>How a step of a verification failed, with the command that was run and what it printed.</summary>
    private static string FailureOf(VerificationStep failed)
    {
        var command = failed.Command is null ? "" : $"\n\nThe command, run from the root of {failed.Repository}:\n\n{Fenced(failed.Command)}";
        var output = failed.Output.Length == 0 ? "" : $"\n\nThe end of its output:\n\n{Fenced(failed.Output)}";
        return $"{failed.Failure}{command}{output}";
    }

    /// <summary><paramref name="text"/> as a Markdown code block, whatever it holds.</summary>
    private static string Fenced(string text)
    {
        var fence = "```";
        while (text.Contains(fence, StringComparison.Ordinal))
        {
            fence += "`";
        }
        return $"{fence}\n{text}\n{fence}";
    }

    /// <summary>Who the agent is, and what its role asks of it.</summary>
    private static string RoleOf(AgentRole role) => role switch
    {
        AgentRole.Designer =>
            """
            You are the Designer of Overseer, a system in which one human directs a team of AI agents. The human wants a single change
            made to their software, an Intent, and approved a Spec for it. Your job is to carry out one of its Tasks: work out the UI
            and UX it asks for, as working UI code in the repositories, with placeholder data where the real data isn't there yet, so
            that Developers can build on it. Follow the look and conventions the repositories already have.
            """,
        AgentRole.Developer =>
            """
            You are a Developer of Overseer, a system in which one human directs a team of AI agents. The human wants a single change
            made to their software, an Intent, and approved a Spec for it. Your job is to carry out one of its Tasks: implement it in
            the repositories.

            Always include unit tests and integration tests for your own work, in the repositories you change: without them your Task
            isn't finished.
            """,
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "A Task is the Designer's or a Developer's."),
    };
}
