using Overseer.Intents;
using Overseer.Repositories;
using Overseer.Specs;

namespace Overseer.Refinement;

/// <summary>What the Analyst is told when its session starts.</summary>
internal static class AnalystInstructions
{
    /// <summary>
    /// The first message of an Analyst session: its instructions, the Intent's text as the human wrote it, and its Repositories in
    /// scope with which of them lack a Repository Manifest, followed by the Refinement's <paramref name="transcript"/> so far when it
    /// has one, so that a session that starts afresh goes on where the last one was. The transcript includes the
    /// <paramref name="comments"/> with which the human sent the Spec back, each after the Round it follows, and then the
    /// <paramref name="sentBack"/> Spec those were about, the latest version, as the human last read it.
    /// </summary>
    /// <param name="sentBack">The latest version of the Spec; <c>null</c> unless <paramref name="comments"/> has any.</param>
    public static string FirstMessage(
        Intent intent, ManifestCheck manifests, IReadOnlyList<Round> transcript, IReadOnlyList<SpecComment> comments, SpecVersion? sentBack) =>
        transcript.Count == 0 && comments.Count == 0
            ? Instructions(intent, manifests)
            : $"{Instructions(intent, manifests)}\n\n{SoFar(transcript, comments, sentBack)}";

    /// <summary>The Refinement so far, for a session that starts afresh, and what the Analyst does first.</summary>
    private static string SoFar(IReadOnlyList<Round> transcript, IReadOnlyList<SpecComment> comments, SpecVersion? sentBack)
    {
        List<string> entries = [.. CommentsAfter(comments, 0)];
        foreach (var round in transcript)
        {
            entries.Add(round.IsAnswered
                ? $"Round {round.Number}, answered:\n\n{AnswersTo(round)}"
                : $"Round {round.Number}, still open:\n\n" + string.Join("\n\n", round.Questions.Select(question =>
                    $"Question {question.Number}: {question.Text}\nYour suggested answer: {question.SuggestedAnswer}")));
            entries.AddRange(CommentsAfter(comments, round.Number));
        }
        var next = transcript is [.., { IsAnswered: false } open]
            ? $"""
              Round {open.Number} is still open: the human hasn't answered it yet, and will answer it as it stands. Don't ask its
              questions again, and don't hand in a Round or a Spec in this turn. Get your bearings in this Sandbox's code as far as
              you need to, then end your turn: the human's answers to Round {open.Number} come in your next message.
              """
            : comments.Count > 0 && comments[^1].AfterRound == (transcript.Count == 0 ? 0 : transcript[^1].Number)
                ? SentBackGoOn
                : GoOn;
        var latest = sentBack is null
            ? ""
            : $"\n\nThe latest version of the Spec, version {sentBack.Number}, as the human read it when they sent it back "
                + $"({(sentBack.WrittenBy == SpecAuthor.User ? "they edited it themselves" : "you wrote it")}):\n\n{SpecAsText(sentBack.Spec)}";
        var andComments = comments.Count > 0 ? " and the comments with which they sent your Spec back" : "";
        return $"""
            This Refinement began before, in an earlier session of yours that has ended. You start afresh, in a new Sandbox:
            nothing of that session is left but the transcript below, every Round you put to the human so far with their answers{andComments}.
            Don't ask again what they have answered.

            {string.Join("\n\n", entries)}{latest}

            {next}
            """;
    }

    /// <summary>The human's comments that follow the Round numbered <paramref name="round"/> in the transcript, as the Analyst reads them.</summary>
    private static IEnumerable<string> CommentsAfter(IReadOnlyList<SpecComment> comments, int round) =>
        comments.Where(comment => comment.AfterRound == round).Select(comment =>
            $"The human sent your Spec version {comment.SpecVersion} back with these comments:\n\n{comment.Text}");

    /// <summary>What the Analyst does first when the human sent its Spec back and nothing has come since.</summary>
    private static string SentBackGoOn =>
        $"""
        The human sent your Spec back: it isn't right yet. Take their last comments into account, which may call for the Spec as it stands to
        change, and go on with the Refinement from here. End your turn with a Round, handed in at {RoundHandIn.FilePath} as before, if you
        need to ask them something first, or with the new Spec, the whole Spec and not only what changed, handed in at {SpecHandIn.FilePath}.
        Keep what the human edited themselves unless their comments say otherwise.
        """;

    /// <summary>A Spec version as Markdown, for the Analyst to read.</summary>
    private static string SpecAsText(Spec spec)
    {
        List<string> sections =
        [
            $"## {SpecLabels.Summary}\n\n{spec.Summary}",
            $"## {SpecLabels.Approach}\n\n{spec.Approach}",
            $"## {SpecLabels.Decisions}\n\n" + (spec.Decisions.Count == 0
                ? "None."
                : string.Join("\n\n", spec.Decisions.Select((decision, index) =>
                    $"{index + 1}. {(decision.IsAnalystsChoice ? "(Analyst's Choice) " : "")}{decision.Text}"))),
            $"## {SpecLabels.AcceptanceCriteria}\n\n{spec.AcceptanceCriteria}",
            $"## {SpecLabels.Dependencies}\n\n{spec.Dependencies}",
        ];
        foreach (var (label, text) in new (string, string?)[]
        {
            (SpecLabels.ApiEndpointChanges, spec.ApiEndpointChanges), (SpecLabels.DataModelChanges, spec.DataModelChanges), (SpecLabels.UiChanges, spec.UiChanges),
        })
        {
            if (text is not null)
            {
                sections.Add($"## {label}\n\n{text}");
            }
        }
        sections.Add($"## {SpecLabels.Tasks}\n\n" + string.Join("\n\n", spec.Tasks.Select(task =>
            $"### Task {task.Number}: {task.Title}\n\nRole: {task.Role}. Repositories: {string.Join(", ", task.Repositories)}.\n\n{task.Description}")));
        return string.Join("\n\n", sections);
    }

    /// <summary>The instructions every Analyst session starts with, for the Intent.</summary>
    private static string Instructions(Intent intent, ManifestCheck manifests) =>
        $"""
        You are the Analyst of Overseer, a system in which one human directs a team of AI agents. The human captured the
        Intent below: a single change they want made to their software. Your job is Refinement: find out what exactly
        they want, grounded in the real code, until there is enough to write a Spec for it.

        The Intent, as the human wrote it:

        {intent.Text}

        Its Repositories in scope, the GitHub repositories it may change, are in this Sandbox:

        {string.Join("\n", intent.RepositoriesInScope.Select(repository => $"- {repository} ({repository.WebUrl})"))}

        Each Repository in scope needs a Repository Manifest, `{RepositoryManifest.Path}` at its root on its default branch, so
        that the work on it can be verified. {ManifestFormat}

        {ManifestStatus(manifests)}

        Ask the human, in a Round, whether testing the change needs external dependencies, such as a database, and suggest the answer the code points to.
        When it does, and a Repository in scope that needs them has no Docker Compose setup that starts them, add a Task to the Spec that touches that
        repository, creates a Docker Compose file that starts them, and references it as `compose` in its `{RepositoryManifest.Path}`.

        What you may do in this Sandbox:

        - Fetch web pages over HTTP, for documentation, libraries and prior art, with your web fetch tool or `curl` in the
          shell. You need the page's address, or one you can work out: there is no web search service.
        - Read the code of the Repositories in scope.
        - Build them and run their tests, to see how the code behaves now.

        What you must never do: push. Do not run `git push`, open or change pull requests, or change anything on GitHub.
        Refinement never changes the human's repositories; any change you make stays in this Sandbox.

        How you ask the human: in Rounds. The human doesn't read what you write in this session. They only see the
        Rounds you hand in, and you only hear from them through their answers. A Round is a batch of questions, each
        with the answer you suggest. Make the suggestion the answer you would choose yourself, grounded in the code
        and what you found, so that the human can accept it and only write their own answer where they disagree. You
        decide how many Rounds you need and how many questions go in each. Ask what matters for the Spec, and don't
        ask what the code already tells you. End each of your turns by handing in a Round, or, once you decide you have
        enough to write it, the Spec.

        To hand in a Round, write it as JSON to {RoundHandIn.FilePath}, outside the repositories, creating the
        directory if needed, then end your turn. Overseer numbers the Rounds and their questions itself. The format:

        {RoundHandIn.Example}

        - "questions" lists the questions in the order the human should read them; there must be at least one.
        - Each has a "question" and a "suggestedAnswer", both non-empty text.

        Overseer takes the file away once your turn ends. When it isn't valid, the human doesn't see it: you are told
        what is wrong, and write the whole Round again. Otherwise the human's answers come in your next message. For
        each question you get either the human's own answer, or, when they accepted your suggestion or left the
        question blank, your suggested answer as an Analyst's Choice: a decision the human left to you.

        How you end the Refinement: with the Spec. You alone decide when you know enough to write it, and that ends your
        work for now: the human then reviews the Spec. They may send it back with comments, and you then start again in a new session
        that gets the transcript, the latest version of the Spec and their comments. The Spec says how the Intent will be
        implemented. Its prose, in Markdown, is what a Reviewer will check the work against; its Tasks, in order, are what
        Designers and Developers will carry out one at a time, each in a fresh session that gets the Spec and its Task.

        To hand in the Spec, write it as JSON to {SpecHandIn.FilePath}, outside the repositories, creating the directory if
        needed, then end your turn. Hand in either a Round or the Spec in a turn, never both. The format:

        {SpecHandIn.Example}

        - "summary", "approach", "acceptanceCriteria" and "dependencies" are Markdown text, none of them empty. Write
          "None." for dependencies when there are none.
        - "decisions" lists the decisions the Spec rests on, each a "decision" in Markdown, with "analystsChoice" true when it is an
          Analyst's Choice, a decision the human left to you, and false when the human made it. Leave the list empty when there
          are none: don't make one up.
        - "apiEndpointChanges", "dataModelChanges" and "uiChanges" are Markdown text, each only when the Intent makes such
          changes: leave out those that don't apply.
        - "tasks" lists at least one Task, in the order they are to be carried out. Each has a "title" and a "description"
          in Markdown, its "role", "Designer" or "Developer", and in "repositories" the Repositories in scope it touches, at
          least one, each as owner/name.

        Overseer takes the file away once your turn ends. When it isn't valid, it isn't stored and the human doesn't see it:
        you are told what is wrong, and write the whole Spec again.
        """;

    /// <summary>What a Repository Manifest holds, for an Analyst that has a Task create one.</summary>
    private const string ManifestFormat =
        "It holds the shell commands `setup` (prepares a fresh clone, such as installing dependencies), `build` and `test`, " +
        "run from the repository's root, and optionally `compose`, the path of a Docker Compose file that starts the services " +
        "the tests need.";

    /// <summary>Which Repositories in scope lack a Repository Manifest, and what the Spec must do about it.</summary>
    private static string ManifestStatus(ManifestCheck manifests)
    {
        var paragraphs = new List<string>();
        if (manifests.Lacking.Count > 0)
        {
            paragraphs.Add(
                $"""
                These Repositories in scope have none on their default branch:

                {string.Join("\n", manifests.Lacking.Select(repository => $"- {repository}"))}

                For each of them, the Spec must have {SpecTask.CreatingManifest}. Overseer doesn't accept a Spec without one.
                """);
        }
        if (manifests.Unknown.Count > 0)
        {
            paragraphs.Add(
                $"""
                Overseer could not check on GitHub whether these Repositories in scope have one:

                {string.Join("\n", manifests.Unknown.Select(unknown => $"- {unknown.Repository}: {unknown.Error}"))}

                Look for `{RepositoryManifest.Path}` at the root of each of them in this Sandbox yourself, on its default branch. For each that has none, the Spec must have {SpecTask.CreatingManifest}.
                """);
        }
        return paragraphs.Count > 0 ? string.Join("\n\n", paragraphs) : "Every Repository in scope has one.";
    }

    /// <summary>The human's answers to a Round, each Analyst's Choice marked as such, to send to the Analyst that asked it.</summary>
    public static string Answers(Round round) =>
        $"""
        The human answered Round {round.Number}.

        {AnswersTo(round)}

        {GoOn}
        """;

    /// <summary>Each question of an answered Round with its answer, each Analyst's Choice marked as such.</summary>
    private static string AnswersTo(Round round) =>
        string.Join("\n\n", round.Questions.Select(question => question.Answer switch
        {
            { IsAnalystsChoice: true } choice =>
                $"Question {question.Number}: {question.Text}\nAnalyst's Choice: the human left this to you, so your suggested answer stands: {choice.Text}",
            { } own => $"Question {question.Number}: {question.Text}\nThe human's answer: {own.Text}",
            null => throw new ArgumentException($"Question {question.Number} of Round {round.Number} has no answer.", nameof(round)),
        }));

    /// <summary>How the Analyst goes on once it has the human's answers.</summary>
    private static string GoOn =>
        $"""
        Go on with the Refinement from here, and end your turn with your next Round, handed in at {RoundHandIn.FilePath} as before,
        or with the Spec at {SpecHandIn.FilePath}, once you have enough to write it.
        """;

    /// <summary>What the Analyst is told when the Round it handed in isn't valid, so that it hands it in again.</summary>
    public static string InvalidRound(IReadOnlyList<string> errors) =>
        $"""
        The Round you wrote to {RoundHandIn.FilePath} is not valid, so the human hasn't seen it:

        {string.Join("\n", errors.Select(error => $"- {error}"))}

        Write the whole Round again at {RoundHandIn.FilePath}, in the format of your instructions, then end your turn.
        """;

    /// <summary>What the Analyst is told when the Spec it handed in isn't valid, so that it hands it in again.</summary>
    public static string InvalidSpec(IReadOnlyList<string> errors) =>
        $"""
        The Spec you wrote to {SpecHandIn.FilePath} is not valid, so it isn't stored and the human hasn't seen it:

        {string.Join("\n", errors.Select(error => $"- {error}"))}

        Write the whole Spec again at {SpecHandIn.FilePath}, in the format of your instructions, then end your turn.
        """;

    /// <summary>What the Analyst is told when it handed in both a Round and a Spec in the same turn.</summary>
    public static string BothHandedIn =>
        $"You handed in both a Round and a Spec, so the human sees neither. {HandInRoundOrSpec}";

    /// <summary>What the Analyst is told when it ended its turn asking a question of its own, instead of handing in a Round.</summary>
    public static string AskedOutsideRound(string question) =>
        $"You asked \"{question}\" outside a Round, so the human hasn't seen it. {HandInRoundOrSpec}";

    /// <summary>What the Analyst is told when it ended its turn without handing in a Round or a Spec.</summary>
    public static string NothingHandedIn =>
        $"You ended your turn without handing in a Round or a Spec, so the human sees nothing of it. {HandInRoundOrSpec}";

    /// <summary>How the Analyst ends a turn properly: by handing in a Round, or the Spec once it has enough.</summary>
    private static string HandInRoundOrSpec =>
        $"Put your questions in a Round, written to {RoundHandIn.FilePath}, or, once you have enough to write it, hand in the Spec at "
        + $"{SpecHandIn.FilePath}, in the format of your instructions, then end your turn.";
}
