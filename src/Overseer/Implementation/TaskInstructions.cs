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
    /// session is for, with the repositories it touches; and, when the Task's last verification failed, how, or when pushing its
    /// work was rejected, why.
    /// </summary>
    /// <param name="task">A Task of the Intent's own Task list, which may differ from the Approved Spec's.</param>
    /// <param name="taskCount">How many Tasks the Intent has.</param>
    /// <param name="failedLast">
    /// The steps that failed the Task's last verification attempt, which an earlier session made; none when none was made, or it
    /// passed.
    /// </param>
    public static string FirstMessage(
        Intent intent, Spec approved, IntentTask task, int taskCount, IReadOnlyList<VerificationStep> failedLast)
    {
        var owesScreenshots = TaskScreenshots.OwedBy(task.Role);
        if (!owesScreenshots)
        {
            // As where the Task was the Designer's when it last failed.
            failedLast = [.. failedLast.Where(step => step.Kind != VerificationStepKind.Screenshots)];
        }
        return $"""
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
        - When you end your turn, Overseer verifies your work itself: {VerifiedBy(task)} What fails comes back
          to you to fix. What passes, Overseer commits where you haven't, and pushes, as it was when you ended your turn: leave
          nothing behind that doesn't belong in the repository, such as build outputs that its `.gitignore` doesn't cover. What
          Overseer's own runs of `setup`, `build` and `test` wrote isn't committed, unless you change it: don't commit it yourself.
        - Leave pushing to Overseer: never run `git push`, don't open or change pull requests, and don't change anything else on
          GitHub. Stay on the Intent branch.
        - Where the Spec and your Task leave something open, decide for yourself.
        - Ask the human a question only when you can't reasonably decide something yourself, as your Task waits until they answer.
          To ask, end your turn with the question as your last message, ending in a question mark and holding what they need to
          know to answer it: their answer comes as your next message.
        - Otherwise don't end your turn with a question, nor with an offer such as "Shall I go on?": Overseer takes a last message
          that ends in a question mark as a question to the human. End your turn with a short statement of what you did and what you
          decided.
        {(owesScreenshots ? $"\n{ScreenshotInstructions}\n" : "")}
        # The Approved Spec

        The human approved this Spec. It is what the work is held against.

        {SpecText.Of(approved)}

        # Your Task

        Your Task is Task {task.Number} of {taskCount}: {task.Title}

        Repositories it touches: {string.Join(", ", task.Repositories)}

        {task.Description}{(failedLast.Count == 0 ? "" : $"\n\n{LastFailure(failedLast)}")}{(
            task.RejectedPush is { } rejection ? $"\n\n{Rejected(rejection, intent.Branch)}" : "")}
        """;
    }

    /// <summary>What a Designer is told about the screenshots it owes.</summary>
    private static string ScreenshotInstructions { get; } =
        $"""
        # Your screenshots

        The human sees your design through screenshots, which Overseer shows with your Task, before Developers build on it. Run what
        you built in this Sandbox and take screenshots of it with the headless browser here: Playwright is installed, with its
        browsers, as in `playwright screenshot --full-page <url> <file>.png`. Show each screen and state your Task designs.

        Hand them in before you end your turn: without them your Task isn't done.

        {ScreenshotHandIn.HowToHandIn}
        """;

    /// <summary>What a new session on a Task is told about the verification that failed before it.</summary>
    private static string LastFailure(IReadOnlyList<VerificationStep> failed) =>
        $"""
        # What failed last time

        The last time Overseer verified work on your Task, before your session, it failed. Your work has to pass where that didn't.

        {FailuresOf(failed)}
        """;

    /// <summary>What a new session on a Task is told about the push of its work that was rejected.</summary>
    private static string Rejected(string error, string branch) =>
        $"""
        # The push was rejected

        Work on your Task passed verification and was committed, but pushing it was rejected, so GitHub doesn't have it:

        {Fenced(error)}

        Deal with what rejected it, in the repository named there:

        - Where the Intent branch on GitHub holds commits that yours doesn't, as when someone else pushed to it, or a commit that
          was on GitHub already has been amended or rebased, run `git fetch origin` and merge `origin/{branch}` into your
          branch. Keep every commit that is on GitHub as it is: Overseer will never force push.
        - Where a hook or a rule of the repository refused the work, change what it objects to. You may rewrite commits that aren't
          on GitHub yet where it objects to those.

        Leave pushing to Overseer, which verifies your work again and then pushes it.
        """;

    /// <summary>What a Task's session is told when its work failed verification, for it to fix.</summary>
    /// <param name="failed">The steps of the verification that failed.</param>
    /// <param name="attempt">Which verification attempt of the session that was, from 1, as the Portal numbers it.</param>
    /// <param name="failures">
    /// How many of the session's attempts have failed, that one included, of the <paramref name="allowed"/> ones: fewer than
    /// <paramref name="attempt"/> where an earlier one passed.
    /// </param>
    public static string VerificationFailed(
        IntentTask task, IReadOnlyList<VerificationStep> failed, int attempt, int failures, int allowed) =>
        $"""
        Overseer verified your work on Task {task.Number}, and attempt {attempt} failed. Failed attempts so far: {failures}, of the {allowed} allowed.

        {FailuresOf(failed)}

        Fix it, and end your turn as before. Overseer then verifies your work again.
        """;

    /// <summary>What a Task's session is told once the human has answered its question.</summary>
    public static string Answer(IntentTask task, string answer) =>
        $"""
        The human answered your question:

        {answer}

        Go on with Task {task.Number}, and end your turn as before.
        """;

    /// <summary>What a Task's session is told with a message the human sent it while it worked.</summary>
    public static string MessageOfTheHuman(IntentTask task, string message) =>
        $"""
        The human sent you a message while you worked:

        {message}

        Take it into account, go on with Task {task.Number}, and end your turn as before.
        """;

    /// <summary>The description of the fix-up Task, in Markdown: why there is one, and what failed.</summary>
    /// <param name="failed">The steps of the final verification that failed.</param>
    public static string FixUp(IReadOnlyList<VerificationStep> failed) =>
        $"""
        Once the Tasks before this one were Done, Overseer ran the final verification, the full build and tests on the Intent
        branch, and it failed. Make it pass.

        {FailuresOf(failed)}
        """;

    /// <summary>How each of these steps of a verification failed, with the command that was run and what it printed.</summary>
    public static string FailuresOf(IReadOnlyList<VerificationStep> failed) => string.Join("\n\n", failed.Select(FailureOf));

    /// <summary>What Overseer runs to verify a Task's work, and in which repositories.</summary>
    private static string VerifiedBy(IntentTask task) => task.IsFixUp
        ? $"""
          it runs those commands in every Repository in scope,
            also those your Task doesn't list, after `setup` where you changed the manifest or the Compose file. A repository you
            changed, or that an earlier Task changed, must have a Repository Manifest. {RepositoryManifest.Format}
          """
        : """
          it runs those commands in each repository your Task touches,
            and in each other one you changed, after `setup` where you changed the manifest or the Compose file.
          """;

    /// <summary>How a step of a verification failed, with the command that was run and what it printed.</summary>
    private static string FailureOf(VerificationStep failed)
    {
        if (failed.Kind == VerificationStepKind.Screenshots)
        {
            var wrong = failed.Output.Length == 0 ? "" : $"\n\n{failed.Output}";
            return $"{failed.Failure}{wrong}\n\n{ScreenshotHandIn.HowToHandIn}";
        }
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
            that Developers can build on it, and hand in screenshots of it. Follow the look and conventions the repositories already
            have.
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
