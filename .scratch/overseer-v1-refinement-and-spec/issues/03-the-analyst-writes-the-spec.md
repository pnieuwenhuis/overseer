# 03: The Analyst writes the Spec

**What to build:** When the Analyst decides it has enough information, it hands in a Spec instead of another Round. It uses the same file-and-validate mechanism as 02. A valid Spec becomes version 1 of the Intent's Spec. The Intent moves from Refining to AwaitingSpecApproval. The Refinement Sandbox is destroyed, the slot is freed and the light turns orange because a Spec awaits approval. The detail page shows the Spec. Covers user stories 48, 54, 55 and 56. It also covers story 42 for AwaitingSpecApproval.

The **Spec** module from the spec starts here. A Spec version has two parts: prose Markdown sections and a structured, ordered Task list. The prose sections are:

- summary
- approach
- decisions, each marked when it is an Analyst's Choice
- acceptance criteria
- dependencies
- API endpoint changes, data model changes and UI changes, each only when it applies

Each Task has a title, a description, a role (Designer or Developer) and the Repositories in scope it touches. The Task list is what the Task Runner will execute. The prose is what the Reviewer will check against.

An invalid Spec is never stored. The Analyst gets the validation errors and tries again. A Spec is invalid when:

- a required section is missing
- there are no Tasks
- a Task lacks a title, description or role
- a Task has a role other than Designer or Developer
- a Task touches no repository, or a repository that isn't in scope

**Blocked by:** 02 (Rounds: the Analyst asks, the user answers)

**Status:** done

- [x] A turn that ends with a valid Spec stores it as Spec version 1 and moves the Intent to AwaitingSpecApproval through the Intent Lifecycle
- [x] The Refinement Sandbox is destroyed and the Analyst session stopped once the Spec is stored
- [x] An Intent awaiting Spec approval takes no Active Intent slot (new test for story 42) and shows an orange light labelled Needs Attention
- [x] The detail page shows the Spec's sections rendered as Markdown, with Analyst's Choices visibly marked and the optional sections only when present, followed by the ordered Task list with each Task's title, description, role and repositories
- [x] The transcript stays readable alongside the Spec
- [x] An invalid Spec is never stored; the Analyst gets the validation errors, and the Intent stays Refining
- [x] Working Time stops accruing when the Intent leaves Refining
- [x] Covered by tests through the application with the fake Sandbox Provider
