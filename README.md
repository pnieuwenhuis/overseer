# Overseer

Overseer lets one person direct a team of AI agents, installed into their own Azure subscription. Its Portal lets you capture Intents, refine them into Specs with an Analyst agent, have a Squad of agents implement them in Sandboxes, and review and merge the result. See [the v1 spec](docs/specs/overseer-v1.md), the [glossary](GLOSSARY.md) and the [ADRs](docs/adr/).

Overseer is a single `overseer` executable (ADR 0004). `overseer serve` runs the Portal and the orchestration; `install`, `uninstall` and `reset-password` manage the Azure deployment. Run `overseer` without a command to list them, and `overseer --version` for its version.

## Installing Overseer in Azure

You need the [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), logged in (`az login`) to the subscription to install into, as Owner of it (or Contributor plus User Access Administrator: install creates a role assignment). Then run a released `overseer`:

```sh
overseer install [--region westeurope] [--prefix overseer]
```

Install shows the subscription it uses (`az account set` picks another), then creates everything, which takes 15 to 25 minutes, and prints the Portal URL, the username and a freshly generated password. Only the password's hash leaves your machine, as a Container App secret (ADR 0005); the password is shown only then.

`--prefix` (3 to 12 lowercase letters, digits and hyphens) starts every resource name and identifies the installation. It creates:

- **`<prefix>-rg`**: a Container Apps environment (`<prefix>-env`, logging to `<prefix>-logs`) on the VNet `<prefix>-vnet`; the Container App `<prefix>`, one replica of `ghcr.io/pnieuwenhuis/overseer` at the CLI's own version; PostgreSQL Flexible Server `<prefix>-db-<suffix>` (Burstable, private on the VNet, Microsoft Entra authentication only); Key Vault `<prefix>-kv-<suffix>`; and the user-assigned Managed Identity `<prefix>-identity`. Key Vault and PostgreSQL names must be unique across Azure, so `<suffix>` is derived from the subscription ID: the same prefix always gives the same names.
- **`<prefix>-sandboxes-rg`**, for Sandboxes, with the VNet `sandboxes` (10.41.0.0/16) they go on, peered both ways with `<prefix>-vnet`, and the NAT Gateway `sandboxes-nat`, with its public IP `sandboxes-nat-ip`, that its subnet reaches the internet through. Sandboxes have no public IP, and new VNets no longer get default outbound access, so the NAT Gateway, a small fixed monthly cost, is how they reach GitHub, model providers and package registries. The VNet is in this group, not in `<prefix>-rg`, because the Managed Identity, which creates the Sandboxes, has rights only here.

The Container App runs Overseer with the Azure Sandbox Provider in deployed mode (see [Sandboxes](#sandboxes)): as the Managed Identity, in `<prefix>-sandboxes-rg`, without public IPs. Install also registers `Microsoft.Compute` in the subscription, which the identity can't.

The Managed Identity's only Azure role assignment is Contributor on the Sandbox resource group. Its other access is data-plane only: a Key Vault access policy for secrets (the vault uses access policies instead of Azure RBAC for exactly this reason), and being the only PostgreSQL login, as the server's Entra administrator. The application logs in to PostgreSQL with a token for the identity, so no database password exists.

The scripts install runs are embedded in the executable (`src/Overseer/Installer/Scripts`) and manage Azure only through the Azure CLI. A failing step stops install with the Azure error and, for missing permissions, quotas and unavailable regions, what to do about it. A development build refuses to install, because no image is published for `0.0.0-dev`.

### Upgrading

To upgrade, get the new `overseer` and run `install` again with the same `--prefix`. Install finds the existing installation by its resource group `<prefix>-rg`, tagged `overseer-prefix=<prefix>`. It stops rather than take over a `<prefix>-rg` or `<prefix>-sandboxes-rg` without that tag. It:

- keeps it in its region, so `--region` can be left out. Naming another region is an error, because an installation can't move;
- creates only what is missing, so re-running after an install that stopped partway completes it, and re-running the same version changes nothing. A `sandboxes` VNet that the Azure Sandbox Provider made in local mode gets the NAT Gateway, and a peering that was disconnected (because the VNet on its other side was deleted and created again) is replaced;
- updates the Container App to the image of the CLI's version, with the same settings, and waits for the new revision; the server applies new migrations when it starts;
- leaves the database, the Key Vault with its secrets, and the password alone. It prints the Portal URL and the username, and says the password is kept: `overseer reset-password` sets a new one;
- refuses to deploy a version older than the one running, because migrations only go forward.

### Resetting the password

If you lose the password, generate a new one for the installation with the same `--prefix` as install:

```sh
overseer reset-password [--prefix overseer]
```

It finds the installation in the subscription your Azure CLI uses, sets the new password's hash as the Container App's `password-hash` secret, restarts the active revision and waits until its new replica is ready and the old one, which still has the old hash, is gone. Then it prints the username and the new password, once. The old password no longer works, and sessions logged in with it have ended (ADR 0005). Nothing else changes: no new revision, no other setting, and the database and Key Vault are left alone. It needs Contributor on `<prefix>-rg`.

If the restart fails, it still prints the password, with the `az` command to restart the Container App yourself: the hash is set, and the next restart makes it the only one that works. Running reset-password again would replace it.

After 5 failed logins in a row, the Portal refuses every login for 15 minutes, even one with the correct password, and its login page says until when. A wrong username counts as a failed login too. The count is kept in memory only, so a restart (including the one reset-password does) also ends a lockout.

### Uninstalling

To remove an installation, and stop paying for it, run uninstall with the same `--prefix` as install:

```sh
overseer uninstall [--prefix overseer] [--yes]
```

It finds the installation in the subscription your Azure CLI uses and lists what it removes. Only after you type the prefix (or with `--yes`, for scripts) does it remove it:

1. the Managed Identity's role assignments, so the running Overseer loses its rights on the Sandbox resource group first, and none are left orphaned;
2. the peering between `<prefix>-vnet` and the `sandboxes` VNet, both ways;
3. `<prefix>-sandboxes-rg`, with every Sandbox still in it, their VNet and the NAT Gateway;
4. `<prefix>-rg`, with the Portal, the database and the Key Vault with its secrets. This takes 10 to 20 minutes. Its Log Analytics workspace is deleted for good first: deleting the group would only soft-delete it, and a later install would recover the old logs;
5. the soft-deleted Key Vault, purged, so that a later install with the same prefix can create it again.

This can't be undone. It removes only resource groups tagged `overseer-prefix=<prefix>`: a group of the same name without that tag isn't Overseer's, and uninstall says so and leaves it alone. With nothing installed it says there is nothing to remove, and succeeds. A failing step stops it with the Azure error; running it again removes what is left. It needs Owner on the subscription, or Contributor plus User Access Administrator, as install does.

## Running Overseer locally

You need the .NET 10 SDK and Docker.

1. Start PostgreSQL:

   ```sh
   docker compose up -d
   ```

   This starts PostgreSQL on host port **5432**, with a named volume so data survives restarts. `docker compose down -v` throws the data away.

2. Generate a password:

   ```sh
   dotnet run --project src/Overseer -- reset-password --local
   ```

   This prints the username and a new password, once, and writes only the password's hash to the local config file (see [Configuration](#configuration)). Run it again whenever you lose the password, then restart the server. Without a hash, `serve` refuses to start (ADR 0005).

3. Run the server:

   ```sh
   dotnet run --project src/Overseer
   ```

   The `serve` launch profile runs `overseer serve` in the Development environment, which points at the compose database. Open <http://localhost:5080> and log in. The Portal shows the running version in its header.

Schema migrations are applied at startup, so an empty database gets the schema on the first run and later runs change nothing.

### Portal styles

The Portal is styled with [Tailwind CSS](https://tailwindcss.com) v4. Its source is `src/Overseer/Styles/app.css`: the colour tokens (with their dark-mode values, which follow the operating system), the base styles of headings and form controls, and the classes of the Portal's own parts, such as `intent-state` and `markdown-preview`. Pages and components use Tailwind's utility classes for layout and these classes for the rest; they have no `.razor.css` files of their own. Tailwind builds `src/Overseer/wwwroot/app.css`, which is committed, so building, testing and the image need no Node.js. After changing the source or a class in a `.razor` file, rebuild it with Node 24:

```sh
npm ci
npm run css         # or `npm run css:watch` while you work
```

The release workflow fails when the committed `app.css` differs from what the source builds. Tests find some elements by their exact `class` attribute (such as `class="intent-state"`), so style those through their class in the source, never with utilities in their markup. The font, Schibsted Grotesk, is served from `wwwroot/fonts` under the SIL Open Font License.

## Backlog

The Portal's Home page is the Backlog: the Intents under its filter, newest first. Each is listed by the first line of its text that says anything, without Markdown's heading markers, because an Intent has no title of its own, and with its state and light. Server-side code captures and reads Intents through `IntentLifecycle`, which owns their state.

The Backlog opens on the Open filter: every Intent not yet Done or Cancelled. The Closed filter shows those that are, and stays empty until those states can be reached. The address says which is shown (`/?filter=closed`).

An Intent's light says at a glance how it is doing: green with the label Working while agents work on it, orange with the label Needs Attention when it can't go on until you act, so far while the Analyst waits for your answers to a Round, while a Spec awaits your approval, while a Task's agent waits for your answer to its question, and while an Intent is In Review, and red with the label Failed when it has failed and waits for you, so far when its Refinement or its implementation failed (see below). Red outweighs orange. The label makes it readable without the colour. States in which nothing runs show no light. `IntentLifecycle.LightOf` derives it in one place, from the Intent's state and what the modules that work on Intents report through `IIntentFailures` and `IIntentAttention`: each such module registers its own, and the lifecycle reads them all.

Every Intent but a Draft shows its Working Time, in whole hours and minutes ("2 h 15 min"): the total time it has spent in the Active states. Time as a Draft, Awaiting Spec Approval, Approved or Paused doesn't count. `IntentLifecycle` records every transition it makes, with its time from the injected `TimeProvider`, in the `IntentTransitions` table, and derives Working Time from that history and nothing else. A transition changes the state only through `IntentLifecycle.TransitionAsync`, which records it. The Intents captured before the history was recorded count their Working Time from that upgrade: the first `serve` after it records their start at its own time, once the migrations are applied.

The Backlog and the detail page update live, over Blazor's SignalR connection: `IntentLifecycle`, `Refinements` and the `TaskRunner` announce each change once it is committed, and the page reads again. A detail page stops offering Start, Edit and Delete as soon as the Intent is no longer a Draft, and Start, or Resume on a Paused Intent, goes disabled, with the reason, as soon as the last slot is taken. The Working Time of an Active Intent advances as each minute passes, without anything announced. A change in Settings shows when the page is next opened or an Intent next changes.

New Intent opens an editor for the Intent's text, in free Markdown. Preview renders what is in the editor without saving it, the same way as the Review Guidelines (see [Settings](#settings)). Save as Draft stores the Intent in PostgreSQL, with LF line breaks, and returns to the Backlog; text with nothing but whitespace is refused. A Draft is only captured: no agent works on it.

The page also offers every Configured Repository as a Repository in scope, all selected when it opens. Preview and a refused save keep your selection. They are stored with the Intent, as the repositories are named at that moment, and the Backlog shows them. A Draft can have none, so the Backlog can be filled before any repository is configured. When a selected repository was removed after the page opened, Preview and any save say so, naming it, and a save is refused rather than silently saving a smaller scope: the page then offers the current repositories and keeps your text and the rest of your selection, so saving again goes on without it.

Clicking an Intent in the Backlog opens its detail page, with its rendered text, state and Repositories in scope. An address that names no Intent, or a malformed one, gets the Not Found page.

While the Intent is a Draft, the detail page offers Edit and Delete. Edit opens the same editor as New Intent, starting from the stored text and Repositories in scope, and refuses and keeps what you entered in the same cases. Delete opens a page that asks for confirmation, and only its button deletes. The Intent is then gone from the Backlog and from PostgreSQL, with its Repositories in scope, so they no longer hold those Configured Repositories back from removal; it isn't Cancelled.

`IntentLifecycle` decides on an edit or delete under a lock on the Intent's row, so what counts is the Intent's state at the moment of writing, not when the page was opened. Anything but a Draft is refused, and so is an Intent that was deleted in the meantime; an editor keeps your text so that it isn't lost. A change of state has to update that row, so that an edit or delete under way and the change wait for each other.

A Draft's detail page also offers Start, which moves it to Refining: the only way out of Draft besides deleting. From then on the Intent's text and Repositories in scope can't be edited, and it can't be deleted. The server refuses that as above, whatever a request says, and it refuses a Start from any state that Start doesn't move an Intent on from.

Start is disabled, with the reason below it, while the Draft has no Repository in scope or all Active Intent slots are taken. The server refuses with the same explanation, which says which Intents take the slots and that you can raise the maximum in Settings. An Intent takes a slot in the Active states, Refining, Implementing and In Review, and `IntentLifecycle` defines them in one place; a Draft or a Repository Setup never takes one. Lowering the maximum below the number of Active Intents leaves them as they are: Start simply stays disabled until a slot is free.

A Start holds a PostgreSQL advisory lock from before it counts the Active Intents until it commits, after locking the Intent's row. Under READ COMMITTED a count doesn't see a slot another transaction has taken but not committed, so without the lock two Starts could both take the last one. Any later transition into an Active state has to take the lock the same way.

### Refinement

Starting an Intent launches its Refinement: once the move to Refining is committed, `IntentLifecycle` raises `Transitioned`, its hook for the side effects of transitions, and `Refinements` works on the Intent in the background. It changes the Intent's state only through `IntentLifecycle`, once the Analyst hands in the Spec or the Refinement has been idle for too long, and the lifecycle knows nothing of it: a failed Refinement turns the light red through `IIntentFailures`, which `Refinements` implements and `IntentLifecycle.LightOf` reads. It creates a Sandbox owned by the Intent, holding exactly its Repositories in scope, on the machine the Settings say, and once that is ready starts an Analyst agent session there, with the current agent harness configuration and model provider tokens, like any agent session. The Analyst's first message holds its instructions, the Intent's text and its Repositories in scope. The instructions allow fetching web pages over HTTP (OpenCode's `webfetch` tool, or `curl` in the shell: there is no web search service), reading the code, and building and running the tests in the Sandbox, and forbid pushing. Overseer's default `opencode.json` allows the Analyst all of that without asking (see [Settings](#settings)) and denies it `git push` and `git push *`: OpenCode applies the last bash rule that matches. That only catches a push written that way, and you can edit the configuration, so it is no guarantee.

The detail page of a Refining Intent shows where its Refinement stands, and updates live: provisioning the Sandbox, which takes minutes in Azure, then the Analyst working, both with a green light. `Refinements` keeps that phase in the `Refinements` table, and runs in the background through `SandboxedWork`, as Repository Setups do.

The Analyst questions you in Rounds. It hands one in by writing it as JSON to `/home/overseer/.overseer/round.json` in its Sandbox, outside the repositories, and ending its turn; its instructions describe the format. Overseer then takes the file through the Sandbox Provider (`ISandboxProvider.TakeFileAsync` reads and removes it) and validates it. That mechanism is `HandIn<T>`, which the Spec uses too. An invalid Round is never shown: the Analyst is told what is wrong and writes it again, and a turn that hands in nothing, or both a Round and a Spec, gets the same treatment. After 3 turns in a row without a valid Round or Spec (`Refinements.HandInAttempts`) the Refinement fails, rather than spend the Analyst's model on it for good. A valid Round is numbered (Round 1, Round 2, ...) and stored with its numbered questions and suggested answers in the `Rounds` and `RoundQuestions` tables, and the Intent Needs Attention while the detail page offers the Round as a form. For each question you write your own answer, tick As suggested, or leave it blank; the last two record the suggested answer as an Analyst's Choice. A question with both an answer of your own and As suggested ticked refuses the answers, keeping what you typed in the form, rather than drop either. Sending the answers records them and passes them to the same Analyst session, each Analyst's Choice marked as such, and the light turns green again. The detail page shows every Round with its answers, also after a restart. Answers are taken once, and only for the Round that is open: the `Refinements` row serializes two submissions, and the second is refused.

The Analyst alone decides when it has enough to write the Spec, and then hands it in instead of a Round, as JSON at `/home/overseer/.overseer/spec.json` (`SpecHandIn`); its instructions describe the format. A Spec has prose sections in Markdown (summary, approach, decisions each marked when it is an Analyst's Choice, acceptance criteria, dependencies, and API endpoint, data model and UI changes only when they apply) and an ordered list of Tasks, each with a title, a Markdown description, a role (Designer or Developer) and the Repositories in scope it touches. It is invalid when a required section is missing, it has no Tasks, or a Task lacks a title, description or role, has another role, or touches no repository or one that isn't in scope. An invalid Spec is never stored: the Analyst is told what is wrong, as for a Round, and the Intent stays Refining. A valid one ends the Refinement: `IntentLifecycle.AwaitSpecApprovalAsync` stores it as the Intent's Spec version 1, through `SpecStore`, in the `SpecVersions`, `SpecDecisions` and `SpecTasks` tables, and moves the Intent to AwaitingSpecApproval in the same transaction. The Analyst session stops and the Sandbox is destroyed. AwaitingSpecApproval isn't an Active state, so the Intent frees its slot and its Working Time stops, and its light is orange (Needs Attention), which `IntentLifecycle` derives from the state itself. The detail page shows the Spec, its sections rendered as Markdown and its Tasks in order, above the Refinement transcript.

Refinement makes sure the Squad can verify its work. Before the Analyst's first turn, also after a Retry, `Refinements` asks GitHub, through the GitHub Gateway, whether each Repository in scope has a Repository Manifest, `.overseer.yml`, on its default branch (which needs "Contents: Read"); it only checks that the file is there, and doesn't read it (ADR 0003). The Analyst's first message names those that have none, or says that all have one. A Spec must then have, for each of them, a Task that touches it and names `.overseer.yml` in its title or description; otherwise it is invalid, naming the repositories, and the Analyst is told so as for any invalid Spec. When GitHub can't tell, because there is no GitHub token or GitHub fails, the Refinement starts anyway: the Analyst is told the check failed, with why, and to look for the manifest in its Sandbox itself, and no Spec is refused for those repositories. The instructions also tell the Analyst to ask you, in a Round, whether testing the change needs external dependencies such as a database, and when it does and a repository has no Docker Compose setup for them, to add a Task that creates one and references it as `compose` in `.overseer.yml`.

When the Sandbox can't be created, or the Analyst's session fails, the Intent stays Refining and keeps its slot, its light turns red, and the detail page shows the error (the provider's own, for a Sandbox) and a Retry button. Retry launches the Analyst again in a fresh Sandbox, turning the light green again, and it goes on from the transcript (see below). A Refinement's Sandbox is destroyed as soon as it fails. A Sandbox is recorded before the provider is asked to create it, so one a crash interrupts is found too, and one whose destruction fails stays recorded until a later start destroys it.

A Refinement waiting on you doesn't keep a machine running indefinitely. When a Round stays unanswered for longer than the Refinement idle timeout (see [Settings](#settings), 30 minutes by default), `Refinements` pauses the Intent through `IntentLifecycle.PauseAsync`: the Intent moves to Paused, the Analyst session stops and the Sandbox is destroyed. Only the time a Round waits for you counts, from when it is shown, not the time the Analyst works before it. The timeout runs on the injected `TimeProvider`, and is read again whenever the operating limits are saved (`SettingsStore.OperatingLimitsSaved`), so a change applies to Rounds already waiting: lowering it below the time one has waited pauses that Intent at once. The idle clock lives in memory only: a restart of `serve`, like Resume, offers the open Round again with a fresh idle clock, so the time it waited before doesn't count. The pause and answers given at the same moment are serialized on the `Refinements` row, so only one of them goes through; the pause sets the row to provision a Sandbox, what the Refinement does first once it continues. A Paused Intent isn't Active: it frees its slot, shows no light, and its Working Time stops. Its detail page says so and offers Resume, which `IntentLifecycle.ResumeAsync` handles like Start: it needs a free slot, and while every slot is taken the button is disabled with Start's explanation, and the server refuses it the same way. Resume moves the Intent back to the state it was paused from, which the transitions record. Only Refinement takes a resumed Intent up so far, so Resume is offered, and accepted, only on an Intent paused while Refining (`IntentLifecycle.ResumableStates`).

Every Analyst session starts afresh from the transcript, so a Refinement goes on where it was whatever ended its last session: Resume, Retry, or a restart. The session's first message holds the instructions and the Intent's text as always, followed, when there is a transcript, by every Round so far with its answers and Analyst's Choices, and the Round still open, if any, with its suggested answers. You don't repeat anything. An open Round stays open rather than being asked again: the Analyst is told which one it is and to end its turn without handing anything in, once it has caught up, and the Round is then offered as a form again (and the idle timeout counts from then). Until then its questions show as not answered yet, and on a Paused Intent the page says to Resume to answer them. When `serve` stops during a Refinement, its Sandbox is destroyed; the next start, before the Portal takes requests, destroys any Sandbox an Intent left behind (`LeftOverSandboxes`) and sets each interrupted Refinement to provision a Sandbox, so that no answers come for a session that is gone, and continues each Refinement in a fresh Sandbox from its transcript. A failed Refinement stays failed until its Retry.

While an Intent awaits Spec approval you can make small corrections to the Spec yourself: Edit, beside the Spec on the detail page, opens its editor at `/intents/{id}/spec/edit`. You change the prose sections and each Task's title, description, role and repositories, add and remove Tasks, and move them up or down. You also edit, add and remove decisions: a decision stays an Analyst's Choice only while its text is unchanged, and one you change or add is yours. Moving, adding and removing only change the form, and nothing is stored until you save. Saving adds a new Spec version, marked as edited by you, and keeps the earlier ones: the detail page shows the latest, and links each version, marked as written by the Analyst or edited by you, to a page of its own. `SpecEdits` checks an edit with the same `SpecHandIn` validation as the Analyst's Spec, writing it in the Analyst's format, including the Repository Manifest Tasks, against what GitHub says at that moment; an invalid edit is refused with every reason, in the form's words ("Task 2's title is empty.") where the Analyst gets its JSON's (`SpecWording`), keeping what you entered, and stores nothing. Editing is refused, also for a form posted directly, unless the Intent awaits Spec approval at that moment (`IntentLifecycle.EditSpecAsync`, under the lock on the Intent's row). Saves are optimistic: the editor sends the version it started from, and a save is refused when another version was saved meanwhile. The form then holds that newer version, now based on it, so that saving again can't undo what the other save wrote; your own changes are shown below it, read-only and marked as not saved, for you to enter again what you still want.

If the Spec isn't right, Send back, below the Spec on the detail page, sends it back to Refinement instead: the comments field is required, and the Intent moves from AwaitingSpecApproval to Refining (`IntentLifecycle.SendSpecBackAsync`, through `SpecSendBack`). Refining is an Active state, so sending back needs a free slot: while every slot is taken the button is disabled with the explanation of Start, and the server refuses it the same way, also for a form posted directly, as it does unless the Intent awaits Spec approval at that moment. The comments are about the version you read, which must still be the latest, like a save of an edit; they are stored with the Round they follow (`SpecComments`) and shown in the transcript on the detail page between the Rounds before and after them, headed with the Spec version they were about. A refusal keeps what you wrote. The Analyst goes on in a fresh Sandbox like after Resume: its first message holds the transcript with your comments in place, then the latest Spec version, with your own edits, as you read it; it may ask further Rounds, and the Spec it hands in next is stored as a new version after the existing ones, with the Intent awaiting approval again.

Approving the Spec locks it: the version you are looking at becomes the Approved Spec (`IntentRecord.ApprovedSpecVersion`), the fixed contract for implementation and review, and only the latest version can be approved, as approving an earlier one would quietly undo later edits (`SpecApproval`, through `IntentLifecycle.ApproveSpecAsync`, under the lock on the Intent's row). With a free slot the button reads "Approve & start" and the Intent moves to Implementing, taking the slot; the server checks the slot again and refuses, approving nothing, if it was taken meanwhile. Otherwise the button reads "Approve", explains why it can't start, and the Intent moves to Approved, which takes no slot and shows no light. An Approved Intent offers Start (the same `start-intent` form as a Draft's), disabled with the usual explanation while every slot is taken, which moves it to Implementing. From then on the server refuses any edit and Send back, and the detail page shows the Approved Spec and its version.

### Implementation

Moving an Intent to Implementing, with "Approve & start" or Start on an Approved Intent, has the Task Runner (`TaskRunner`, hooked into `IntentLifecycle.Transitioned` as `Refinements` is) work on it in the background. It first gives the Intent its Task list, then prepares a Sandbox for the Squad (`SandboxPreparation`), and then has the Squad carry out the Tasks there (see [Tasks](#tasks)), each verified before it is pushed (see [Verifying and pushing](#verifying-and-pushing)). Once all are Done, it verifies the whole of the work, opens its pull requests and moves the Intent to In Review (see [Final verification and pull requests](#final-verification-and-pull-requests)). Preparing creates a new Sandbox owned by the Intent, with push access, holding its Repositories in scope, on the machine the Settings say. Then, over all Repositories in scope:

1. Each is put on the Intent branch, `overseer/intent-<Intent id>`, the same name in every repository for the Intent's life (`Intent.Branch`): checked out from GitHub when it already exists there, and otherwise created from the default branch, which needs a commit: a repository without one, such as an empty one, fails the preparation, which says to add a first commit to it on GitHub and Retry, as GitHub would make the Intent branch its default branch and no pull request could be opened. Nothing is pushed yet. The clone is also given the identity commits in it are made with, `Overseer <overseer@overseer.invalid>`, as a Sandbox has none of its own, and remembers the commit it is on as the last verified work, in the ref `refs/overseer/verified` (`IntentBranches`). Its Repository Manifest is read from the Sandbox.
2. The `setup` command of each repository with a manifest runs from the repository's root, as a script that stops at its first failing line and has no input to read. What it prints is collected in a file, so a process it leaves running in the background, such as a server, runs on without holding the preparation up.
3. The services of each manifest's Compose file are started with `docker compose --file <compose> up --detach --wait --wait-timeout 600`, which waits until they run, and are healthy where they have a health check.

What `setup` and Compose wrote in the clones is then set aside, so that it is no part of any Task's work (see [Verifying and pushing](#verifying-and-pushing)).

Each repository's services keep the Compose project name Compose gives them (the file's `name`, or else the name of its directory), so that `docker compose` run in the repository finds them. Starting a project replaces the services it has, though, so when more than one repository has a Compose file, each is first asked its project name (`docker compose config`), and the repositories that share one each start theirs under a name of their own instead, `<name>-<owner>-<repository>` (`ComposeProjects`), given with `--project-name`; `docker compose` run in such a repository needs that `--project-name` too.

A command that hangs turns the Intent red once its time limit passes:

| Command                                       | Time limit                                                                    |
| --------------------------------------------- | ----------------------------------------------------------------------------- |
| Putting a repository on the Intent branch     | 5 minutes                                                                     |
| `setup`                                       | 30 minutes                                                                    |
| Reading a Compose file's project name         | 5 minutes                                                                     |
| Starting Compose services, with their images  | 30 minutes, of which Compose waits 10 minutes for them to run and be healthy |
| `build`, and `test`                           | 30 minutes each                                                               |
| Comparing with git, committing, pushing       | 5 minutes each                                                                |
| Giving the Sandbox the GitHub token           | 5 minutes                                                                     |

A repository without a Repository Manifest is only put on the branch: a Task may still create one. The manifest is a YAML file, `.overseer.yml`, at the repository's root:

```yaml
setup: npm ci
build: npm run build
test: npm test
compose: docker/compose.yml # optional
```

`RepositoryManifest.Read` requires `setup`, `build` and `test`, each non-empty text, and takes `compose` as a path in the repository, relative to its root. Any other key, a key given twice, or anything but one mapping makes it invalid. Every manifest is read before any `setup` runs, so an invalid one fails the preparation at once. `build` and `test` aren't run by the preparation: they verify the Tasks' work.

The detail page of an Implementing Intent shows the phase, live: provisioning the Sandbox, which takes minutes in Azure, putting a repository on the Intent branch, running a `setup`, starting Compose services (each naming the repository), then that the Intent is ready, while the Squad carries out the Tasks, and finally the final verification and the opening of pull requests. The light stays green, except while an agent [waits for your answer](#an-agents-question). The Sandbox stays for the rest of the implementation and the review. Where each implementation stands, and the resource ID of the Sandbox it was prepared in, is kept in the `Implementations` table.

When the Sandbox can't be created, a command fails or hangs, or a manifest can't be read or is invalid, the Intent stays Implementing and keeps its slot, its light turns red, and the detail page shows the error, naming the repository at fault, with the end of a failed command's output (a GitHub token, should the output hold one, replaced by `***`), and a Retry button. The Sandbox is destroyed as the preparation fails. Retry then prepares again in a fresh Sandbox, first destroying the old one should destroying it have failed, or waiting for a [restart](#restarting-during-an-implementation) that is still destroying it; when it can't be destroyed, no other is created and the implementation fails again, saying why. A Sandbox is destroyed by one caller at a time (`SandboxRegistry.DestroyAsync`).

#### Tasks

An Intent has a Task list of its own (`IntentTasks`, the `IntentTasks` table): the Approved Spec's Tasks in their order, each Pending, Running, Done or Failed. It is created as the Task Runner first takes the Intent up, all Pending, and is kept apart from the Spec's, which stays locked: a failing final verification adds a fix-up Task to the end of this list, and only a Failed Task of it can be changed (see [Recovering a Failed Task](#recovering-a-failed-task)). The detail page shows every Task with its state, role, repositories and verification attempts, live.

Once the Sandbox is prepared, the Squad carries out the Tasks one at a time (`TaskLoop`, ADR 0001): always the first one that isn't Done, in a new agent session in that Sandbox for the Task's role, Designer or Developer. The session's first message (`TaskInstructions`) holds the role's instructions, the whole Approved Spec, and the Task with the repositories it touches; when the Task's last verification failed, it also holds what failed, with the command and the end of its output, and when pushing its work was rejected, why (see [Verifying and pushing](#verifying-and-pushing)). The instructions tell a Developer to always include unit and integration tests for its own work, and every agent to run the `build` and `test` commands, that Overseer verifies, commits and pushes the work, to leave pushing to Overseer, and to [ask you a question](#an-agents-question) only when it can't reasonably decide something itself. Overseer's default `opencode.json` allows the Designer and the Developer every command, so only the instructions keep them from pushing. A Task is Done once its work is verified and pushed, and only then does the next one start; after the last comes the final verification. (The fake Sandbox Provider's agents end their turn at once, and its commands succeed without output, so offline every Task is Done right away and the Intent moves to In Review without pull requests.)

A session that can't start or breaks off fails the implementation like a failed preparation, with the reason. The Task goes back to Pending (it is not a Failed Task), and no Task after it starts. Retry then goes on in the same Sandbox, with what the earlier session left there, and runs that Task again in a new session; Tasks that are Done stay Done. It prepares a fresh Sandbox only when that one is gone, such as an evicted spot instance, or after a [restart](#restarting-during-an-implementation), and the work not pushed from the old one is lost.

The GitHub token a Sandbox was created with may have been replaced since, so before each agent session, and before pushing, Overseer gives the Sandbox the token in use (`ISandboxProvider.SetGitHubTokenAsync`): the one git pushes with and `gh` is logged in with. Only a Sandbox with push access takes one. When it can't be given, as when no token is set, the implementation fails, saying why. Whatever comes out of a Sandbox has every GitHub token it may have held since `serve` started replaced by `***`.

#### Following a Task

An agent session reports what its agent does as it happens (`AgentTurn.Updates`), telling the two kinds apart (`AgentUpdate`): more of a message the agent is saying, which arrives in fragments, or a tool it calls, with a later update of the same call once more is known of it. `AcpAgentSession` titles a call as OpenCode does, first by the tool's name and then also by what it is called on, such as `bash: npm test` or `read: src/orders.ts`.

Every session on a Task has a session log (`SessionLogs`, the `AgentSessions` and `AgentSessionEntries` tables): the agent's messages, each put together from its fragments, and its tool calls, in order, with [your messages](#messaging-the-running-agent) where they were delivered, and [its questions](#an-agents-question) and your answers where they came. A question the agent asked in its last message shows once, as its question. A message of the agent's ends at the agent's next tool call, or with its turn. The log is written while the turn goes on, at most about ten times a second, also what a turn reported before it broke off. Every session is kept, those of a retried Task too, so a log can be read after its Task has ended and after a restart. No GitHub token a Sandbox was given, and no model provider token a session was started with, since `serve` started goes into a log: `***` takes its place. What comes whole, your messages and answers and the agents' questions, goes through one place before it is kept, shown or sent (`SandboxRegistry.WithoutSecretsAsync`): it is made storable (no NUL characters, which could split a token), and then loses those tokens and the ones stored at that moment, also one you saved a moment ago. While a message is still arriving, an end of it that such a token begins with is held back until the rest shows whether it is one, and a turn that breaks off leaves it out.

The title of each Task in the detail page's Task list opens its page at `/intents/{id}/tasks/{number}`, which shows the logs of its sessions, live. The running Task in the list also says in one line what its agent is doing: the last line of the message it is saying, or the first line of the tool call it reported last. That line is kept in memory only, and goes as the agent's turn ends: nothing is said while the work is verified or a question awaits your answer.

#### An agent's question

An agent that is truly stuck asks instead of guessing. A turn that ends in a question (see `AcpAgentSession` for how one is told apart) isn't verified: the implementation enters the phase `AwaitingAnswer`, the Intent's light turns orange, and the detail page shows the question under the running Task, with a field to answer it. The Intent stays Implementing, in its slot, with its session and Sandbox, for as long as that takes: there is no idle timeout, which is Refinement's. Your answer goes to the same session (`TaskRunner.AnswerAsync`), after the Sandbox is given the GitHub token in use, the light turns green again and the Task goes on, to verification once a turn ends as done. A question and its answer use no verification attempt, also after a failed verification.

An answer is taken only once, and only for the question that is waiting: the `Implementations` row serializes two submissions, and the second is refused. Every question and answer is kept with its Task (`TaskQuestions`, the `TaskQuestions` table, tokens replaced by `***` [as in a log](#following-a-task), also in the answer the agent gets) and stays on the page, and in the stream of the Task's session. A question whose session ended before it was answered, as at a restart of `serve`, is never answered: the page says so, and the Task's next session isn't told of it.

#### Messaging the running agent

To steer an agent, write to it on the page of its running Task: below the stream is a message box. An agent takes one message per turn and Overseer doesn't interrupt it, so your message waits, shown at the end of the stream, and is delivered to the same session as the next turn once the current one ends, before the work is verified (`SessionLogs.SendMessageAsync`, `TaskLoop`). Several waiting messages each become a turn of their own, in the order you sent them, and verification follows the last.

- A message counts as delivered only once it is written to the agent (`IAgentSession.SendAsync`). One that can't be, as when the agent's process exited or the connection to the Sandbox dropped during verification, fails the implementation like any broken session, and stays not delivered.
- A message sent while the work is verified is delivered before the Task is Done, and the work is verified again after its turn. When that verification fails, the agent is told what failed first, and gets your message after that turn.
- A message uses no verification attempt: only failed attempts count against the allowed ones, so one that passed before your message had the work verified again takes none away.
- A turn that ends in a [question](#an-agents-question) asks it first. A message waiting then, or sent while the question awaits your answer, doesn't answer it: it is delivered after the turn your answer starts, and the Task's page says so.

The box is offered while the session takes messages: from its start until it ends, or its work has passed verification with none waiting, so also not while a verified Task is pushed. A message sent from a page opened earlier, once its session takes no more, is refused, saying why, and the page keeps what you wrote. A message that was accepted but is still waiting as its session ends, because the session broke off, the last allowed attempt failed or `serve` restarted, stays in the stream, marked as not delivered, and the detail page says so under the Task, linking to it; no later session gets it. Until delivered, a message is kept in the `UndeliveredMessages` table. Tokens are replaced in your messages [as in a log](#following-a-task), also in what the agent gets.

#### Verifying and pushing

A Task counts as done only when its tests pass, and a Designer's only with [screenshots](#designer-screenshots) too. Each time its agent's turn ends as done, Overseer verifies the work in the Sandbox (`Verification`), while the session stays open. The fix-up Task is verified by the [full verification](#final-verification-and-pull-requests) instead of the one described here.

First every clone of a Repository in scope has to be on the Intent branch. One that isn't, as when the agent checked out `main` or detached its `HEAD`, is a failed verification that tells the agent so, and nothing is run: Overseer never moves the branch to where the clone is, which could drop the commits it holds.

Then it takes the work: it asks every Repository in scope which files differ from its last verified work (`refs/overseer/verified`): committed since, uncommitted, or untracked and not ignored. What Overseer's own commands wrote doesn't count. After the preparation, and after each verification, whatever a clone holds besides its commits and the work taken before the commands ran is recorded as written by them (`refs/overseer/left`): it stays in the clone, and counts only once something else changes it. A repository with any file that counts is one the Task changed. What is verified is every repository the Task lists, plus any other it changed:

1. Each one's Repository Manifest is read as it is now, since the Task may have created or changed it. A repository without a manifest is skipped; an invalid manifest is a failed verification.
2. Where the changes touch a repository's manifest or its Compose file, its `setup` runs again, and then its Compose services are started again. Its containers are removed first, whatever Compose project they run in (volumes stay), as its project name may have changed. Repositories whose files give the same project name get names of their own, as during preparation; the others' services are left running.
3. `build` and then `test` run for each, in the Intent's order of repositories, like `setup` (a script that stops at its first failing line, without input).

A Task's verification stops at the first thing that fails. A command that fails, or exceeds its time limit, is a failed attempt: the same session is told what failed, with the command and the end of its output, and Overseer verifies again after its next turn. After the allowed number of failed attempts (see [Settings](#settings), 3 by default) the Task is a Failed Task: the Intent turns red, nothing of the Task is pushed, no Task after it runs, and the implementation offers no Retry: you [recover the Task](#recovering-a-failed-task) instead. A Sandbox that no longer answers, or git failing, says nothing about the work: that is no attempt, but fails the implementation like a broken session, and Retry runs the Task again. Attempts are counted per run of a Task, so that a new session has all of them again. They are numbered in the order they are made, also the passed ones, which don't count against the limit: what the agent is told of a failed one, and the Failed Task's error, name it by the number the page shows, and say apart how many have failed.

Every attempt is kept with what was run and the end of each command's output, GitHub tokens replaced by `***`, in the `VerificationAttempts` and `VerificationSteps` tables. The detail page lists them under their Task, each opening to its commands and output, and numbers the runs once a Task has had more than one.

Once an attempt passes, with no [message of yours](#messaging-the-running-agent) waiting, the session stops, and in every repository the Task changed `IntentBranches` commits what was left uncommitted when the agent's turn ended, untracked files too, with the Task's title as the message, on the Intent branch. What `setup`, `build` and `test` wrote isn't committed; anything else lying in the clone that `.gitignore` doesn't cover, such as the outputs of the agent's own builds, is. The repository's pre-commit hooks don't run, as `build` and `test` are what verify the work. Then each of those repositories' Intent branch is pushed, never by force, with the Sandbox's own GitHub credentials, and the commit it is on becomes the last verified work. The branch on GitHub only ever receives verified work, so it is green between Tasks, as long as no agent pushes itself.

A push that fails turns the Intent red with what git and GitHub said. The Task isn't Done, and what Retry does depends on how the push failed:

- When it didn't get through, as when GitHub rejects the token or can't be reached, the Task's page says it is verified and not pushed yet, and Retry pushes again, in every repository it changed, without another session or verification.
- When what was pushed was rejected (`git push` ends with 1), as when someone else pushed to the Intent branch, a commit already on GitHub was amended or rebased, a rule of the repository forbids the work, or a pre-push hook fails, pushing the same again would fail too. The Task's page says the push was rejected, and Retry carries the Task out again, in the same Sandbox: the new agent is told what rejected it, to merge the Intent branch on GitHub where that holds commits its own doesn't, and that Overseer never force pushes. Its work is then verified and pushed as any other.

When the Sandbox is gone by then, or after a [restart](#restarting-during-an-implementation), that work is lost, and the Task is carried out again in a fresh one.

#### Designer screenshots

A Designer's Task produces working UI code with placeholder data, and is done only with screenshots of it, which you look at in the Portal before Developers build on it. There is no approval step. The Designer's instructions ask it to take them with the headless browser in the Sandbox (Playwright), to save them in `/home/overseer/.overseer/screenshots/`, outside the repositories so that they are never committed, and to list them with a caption each in `/home/overseer/.overseer/screenshots.json` (`ScreenshotHandIn`): at most 20 PNG, JPEG, GIF or WebP images, each at most 5 MB.

Each time the agent's turn ends, whether as done, with a question or with a message of yours waiting, and ahead of any verification's commands, Overseer takes the list and the images out of the Sandbox (`TaskScreenshots`; `ISandboxProvider.TakeBinaryFileAsync` brings a file's bytes unchanged). A valid set is kept in the `TaskScreenshots` table in place of the Task's earlier one, so that you see it while you answer a question, and a restart doesn't lose it. The screenshots are required only when the agent says it is done, as part of verifying its work; a question uses no attempt. Without a valid set then, or when anything of one is wrong, such as a file that isn't a whole image of one of those kinds (`ScreenshotImageFormats` checks its structure, so that a broken or truncated one isn't kept) or that holds a token Overseer knows, the verification attempt fails, its other steps still run, and the agent is told what is missing or wrong. Screenshots handed in earlier in the same session still count after a turn that hands in none, as when only the build had to be fixed, unless a later set wasn't valid; what was wrong with a set handed in with a question is said at the next verification. A new session hands in its own, and starts without a list an earlier one left in the Sandbox.

The Portal shows a Task's screenshots under it on the Intent's detail page and on the Task's page, also once the Sandbox is gone. Their images are served to the logged-in user at `/intents/<id>/screenshots/<screenshot>`.

#### Recovering a Failed Task

Once the implementation has stopped at it, a Failed Task, and no other, offers two ways on, under its attempts on the detail page. Both put it back to Pending, turn the light green and take the implementation up again (`TaskRunner.RetryTaskAsync`): a new agent session of the Task's role works on it, told what failed last, with all its verification attempts again. It goes on in the same Sandbox, from what the failed attempts left there; only when that Sandbox is gone, or after a restart, is a fresh one prepared, without that work. The earlier attempts stay on the page, by run.

- **Retry** runs the Task as it is.
- **Edit & retry** opens its editor at `/intents/{id}/tasks/{number}/edit`, with its title, description, role (Designer or Developer) and repositories (at least one, all in scope), and saves what you changed before it retries (`TaskEdits`). The edit is checked as a Task of an edited Spec is, and an invalid one is refused with every reason, keeping what you entered; the rules about a Spec's Tasks together, such as one that creates each missing Repository Manifest, don't apply. Only the Intent's Task changes: the Approved Spec stays as approved, and the work is still held against it.

Both are refused, changing nothing, unless the Intent is Implementing, its implementation has stopped, and the Task is a Failed Task, as after a second press. They are also refused from a page opened before the Task last failed (the page sends the number of the run it showed), so that a stale editor can't undo an edit saved since.

#### Final verification and pull requests

Pull requests are only opened for green code. Once every Task is Done, the Task Runner runs the full verification in the Sandbox (`FinalVerification`): `build` and then `test` of every Repository in scope, in their order. A repository without a Repository Manifest is skipped while it is unchanged, and fails the verification when it is changed, which here means that its work differs from its default branch, since the two parted. A repository that fails doesn't keep the others from being built and tested, so that all that fail are found.

The first final verification that fails adds the fix-up Task to the Intent's Tasks: a Developer Task, "Fix what fails the final verification", whose description holds what failed, with the commands and the end of their output, and which touches the repositories that failed. It runs like any Task. Only its verification differs: it is the full verification, whatever the Task lists, so also after you [recover it](#recovering-a-failed-task) with a changed title, description, role or repositories. As in any verification, `setup` runs again where its changes touch a manifest or a Compose file; a `setup` or Compose start that fails stops it there. The failure that added the Task is none of its attempts. When it doesn't pass within the allowed attempts, it is a Failed Task like any other.

An Intent gets one fix-up Task, and the full verification that Task passes is the final verification: nothing is verified again once it is Done, and that it passed is recorded with it, so that a Retry, also after a restart, goes on to the pull requests. A final verification that can't be run, as when the Sandbox no longer answers, says nothing about the work: it adds no Task, and the implementation fails and offers Retry, which runs it again.

Once it passes, Overseer opens a pull request, from the Intent branch to the default branch, in every repository whose Intent branch changes something of its default branch's since the two parted, and none in the others (`PullRequests`, through the GitHub Gateway). Its title is the Intent's first line, as the Backlog lists it, and its description the summary of the Approved Spec, each cut short where it is longer than GitHub takes. Before opening one, Overseer asks GitHub for a pull request between the two branches in any state: one that exists already, open, closed or merged, is the repository's pull request, and stays as it is, so that a repository never gets a second one. Each one is kept in the `PullRequests` table as it is opened or found. When one can't be opened, the Intent stays Implementing, red, with GitHub's error, and Retry opens only the ones still missing: no Task and no verification runs again, also after a restart. It does so with the Sandbox there is, or, when that one is gone, after preparing a fresh one from the Intent branch, so that an Intent never moves to In Review without a Sandbox to be reviewed in.

With every pull request open, or none to open, the Intent moves to In Review (`IntentLifecycle.AwaitReviewAsync`). It is still an Active Intent, so it keeps its slot and its Working Time goes on, and its Sandbox stays for the review. Its light is orange (Needs Attention), as only you can move it on while no agent works on it, which so far is always (`ReviewAttention`). Its detail page links to each pull request, or says that no repository changed, next to its Tasks and the Approved Spec. Nothing moves an Intent on from In Review yet.

#### Restarting during an implementation

A redeploy or crash doesn't strand an implementation. When `serve` starts, it destroys the Sandboxes left from before (`LeftOverSandboxes`), and the Task Runner carries on with the implementation of every Implementing Intent that hadn't failed, by itself. Where each one stands is recorded before the Portal takes requests.

- The implementation goes on in a fresh Sandbox, prepared from the Intent branch once the old one is destroyed. When the old one can't be destroyed, no other is created: the implementation fails, saying why, and offers Retry.
- Tasks that are Done stay Done. The interrupted Task starts over in a new agent session, with all its verification attempts again; the new agent is told what the last attempt failed with, when it failed. Work that wasn't pushed is lost, also work that had passed verification, as is a question the Task's agent was waiting on. A Task whose work was pushed, but that wasn't recorded Done yet, runs again on top of that work.
- An interrupted final verification runs again.
- Once the final verification had passed, it isn't run again: with the fresh Sandbox prepared for the review, only the pull requests still missing are opened. A pull request that GitHub opened before Overseer could record it counts as opened, also when it has been closed or merged meanwhile.

A failed implementation, including one with a Failed Task, stays failed with its error. It gets its fresh Sandbox when you retry it, also when all that is left is opening pull requests.

## Settings

The Portal's Settings page (in the navigation) holds what you change while Overseer runs. They are stored in PostgreSQL, so they survive restarts and upgrades; secrets are not settings and stay in the secret store (see [Secrets](#secrets)). Server-side code reads them through `SettingsStore`, which goes to the database on every read, so a saved change applies without a restart, and returns a setting's default while none is saved; the Configured Repositories have their own table and are read through `ConfiguredRepositories`.

The operating limits are whole numbers, each from 1 to a maximum. Saving rejects a value that is missing, not a whole number or out of range, says why, and then saves none of them:

| Operating limit                | Default    | Maximum     |
| ------------------------------ | ---------- | ----------- |
| Maximum Active Intents         | 2          | 10          |
| Refinement idle timeout        | 30 minutes | 480 minutes |
| Verification attempts per Task | 3          | 10          |
| Maximum Sandbox lifetime       | 12 hours   | 48 hours    |

Sandboxes are `Standard_D4s_v5` VMs (4 vCPUs, 16 GiB) by default. The Settings page offers `Standard_D2_v5` (2 vCPUs, 8 GiB) instead, and Azure spot instances, which cost less but which Azure evicts when it needs the capacity back. Eviction deletes the Sandbox's VM and disk; its network interface, public IP, network security group and SSH key stay until Overseer destroys the Sandbox. A change applies to the next Sandbox created; existing Sandboxes keep their size and priority. When Azure has no spot capacity or quota for the size, creating a Sandbox fails with Azure's error, as any other creation failure does.

The Review Guidelines are Markdown, empty until you write them; the Reviewer will receive them with every review (`SettingsStore.GetReviewGuidelinesAsync`). Preview renders what is in the editor without saving it, as CommonMark with GitHub's tables, task lists, autolinks and strikethrough. HTML in them shows as text in the preview. They are stored with LF line breaks, and saving nothing but whitespace leaves them empty.

The agent harness configuration is the `opencode.json` every agent session starts with. Each role has an OpenCode agent named after it (`analyst`, `designer`, `developer`, `reviewer`), and each of those agents must set a `model`, as OpenCode names it: `provider/model`. Until you save your own configuration, Overseer's default applies, with Claude Opus for the Analyst and Reviewer and Claude Sonnet for the Designer and Developer. It is written for the OpenCode version Sandboxes install. Agent sessions run headless: no one answers when OpenCode asks for approval, so a session that asks would wait for good. In the default, each agent's own `permission` allows everything it needs that OpenCode would otherwise ask about, namely reading files (`.env` files too), editing and searching them, the shell, fetching web pages (`webfetch`), paths outside the directory OpenCode starts in (`external_directory`) and repeating a tool call (`doom_loop`). There is no global `permission`, so anything else keeps OpenCode's defaults. Keep that in mind when you edit it. Edit the JSON as a whole, or pick a model per role: the picker changes that role's `model` in the JSON and keeps everything else, though it rewrites the JSON indented by two spaces when a model changes. Saving rejects anything other than a JSON object, with the JSON parser's message (which counts lines from 0), and any configuration that lacks a model for a role, naming the role. Nothing is saved then. Restore the default to go back to Overseer's, including later versions' changes to it. Orchestration starts agent sessions through `SandboxRegistry.StartAgentSessionAsync`, which hands the Sandbox Provider the configuration saved at that moment, so a change applies to sessions started after it and never to running ones.

Model provider tokens are the keys for the model providers in `opencode.json`, such as Azure Foundry or an AI gateway, each stored under a name: 1 to 64 letters, digits and dashes, starting with a letter, ignoring case. They are secrets (see [Secrets](#secrets)), each in the secret `model-provider-token-<name>`: the Settings page lists them by name, never by value, and adds, replaces (saving under a stored name) and removes them. Every agent session gets every token stored at its start as an environment variable: the name in capitals, dashes as underscores, followed by `_API_KEY`, so `azure-foundry` gives `AZURE_FOUNDRY_API_KEY` and `anthropic` gives `ANTHROPIC_API_KEY`, the variable that provider reads by default. Reference one in `opencode.json` as `{env:AZURE_FOUNDRY_API_KEY}`, as OpenCode substitutes it. To switch provider, add its token and edit the configuration; Overseer needs no change. A configuration that references a variable no stored token provides is still saved, but the Agents section warns, naming the variable, for as long as that is so.

The GitHub token is the fine-grained token Overseer and its agents work with GitHub as. A new one reaches an implementation's Sandbox before its next agent session or push (see [Tasks](#tasks)). It is a secret (see [Secrets](#secrets)): the Settings page says whether one is set and lets you check it with GitHub, which says the login it works as or why it fails, and lets you set, replace and remove it, but never shows it. Saving a token first checks it with GitHub. A token that works replaces the one in use; one that fails is kept, in memory only, until you store it anyway or discard it, and until then the token in use stays. A restart forgets it.

Give the token access to every repository you configure, with these repository permissions. The check with GitHub says only that the token works, not that it has them.

| Permission                      | Needed to                                                                                                        |
| ------------------------------- | ---------------------------------------------------------------------------------------------------------------- |
| Contents: Read and write        | look for Repository Manifests, clone, and push the Intent branch and a Repository Setup's starter files          |
| Pull requests: Read and write   | find and open an Intent's pull requests                                                                          |
| Administration: Read and write  | create repositories, for a Repository Setup, which needs it on all repositories of the owner; otherwise not needed |

GitHub adds "Metadata: Read-only" itself.

The Configured Repositories are the GitHub repositories that make up the software Overseer builds. Add one as `owner/name` or by pasting its GitHub URL (a page of the repository or a `git clone` URL works too); it is stored as `owner/name` in GitHub's own case. Adding first asks GitHub, through the GitHub Gateway, whether the GitHub token can reach the repository, and refuses it when no token is set, when GitHub knows no such repository that the token can reach (for a private repository GitHub doesn't say whether it is missing or out of the token's reach), or with GitHub's error when GitHub refuses the token. Reaching means reading: whether the token may push is not checked. A repository that is already configured, in whatever case, is refused; the database's key ignores case, as GitHub does. Removing one takes it off the list; nothing changes on GitHub. Removal is refused while an open Intent (any state except Done and Cancelled) has the repository in scope, whatever its case, and the message names every such Intent by its first line, newest first. An Intent that is Done or Cancelled doesn't hold a repository back and keeps naming it, which is why its Repositories in scope have no foreign key to this table. Instead, saving an Intent locks the rows of its repositories (`FOR SHARE`) and removing locks the repository's row (`FOR UPDATE`) before looking at the Intents, so a save and a removal at the same moment can't both succeed.

A Repository Setup creates a new repository instead: give its owner (your login or an organization), name, purpose and tech stack, all required. Overseer creates it on GitHub, private and empty, through the GitHub Gateway, then adds it to the Configured Repositories and keeps the Repository Setup with its purpose and tech stack in PostgreSQL. GitHub creates it for the token's user when the owner is that user, and in the organization otherwise. When GitHub refuses, for instance because the name is taken or the fine-grained token may not create repositories for that owner (that needs "Administration: Read and write" on all repositories of the owner), the Settings page shows GitHub's error and nothing is registered or kept. A Repository Setup is not an Intent and takes no Active Intent slot.

Once the repository is created, an agent writes its starter files in the background. Overseer creates a Sandbox owned by the Repository Setup, holding the new repository, on the machine the Settings say. It starts a Developer agent session there, with the current agent harness configuration and model provider tokens, like any agent session. The agent gets the purpose, the tech stack and instructions to write `README.md`, `AGENTS.md` and the Repository Manifest `.overseer.yml`, and to push them directly to main. No one answers an agent's questions during a Repository Setup, so a question fails it. When the agent is done, Overseer asks GitHub, through the GitHub Gateway, whether `.overseer.yml` is on main (which needs "Contents: Read" on the repository); if it isn't, the Repository Setup fails. The Sandbox is destroyed when the Repository Setup finishes, whether it succeeds or fails.

The Settings page lists the Repository Setups with their phase (provisioning the Sandbox, which takes minutes in Azure, the agent working, done or failed) and updates it live. A failed Repository Setup shows its error and a Retry button, which runs the agent part again in a new Sandbox; the repository is not created again. Agent sessions in Azure Sandboxes come with the Agent Session module, which isn't built yet, so with the Azure Sandbox Provider a Repository Setup fails once its Sandbox is ready. When `serve` stops during a Repository Setup, the next start marks it failed, so it can be retried, and destroys the Sandboxes that Repository Setups left behind.

## Configuration

Configuration is what `serve` needs before it starts, unlike the Settings page. It is layered. Later layers override earlier ones:

1. **Defaults**: `appsettings.json` next to the executable, plus `appsettings.Development.json` in the Development environment.
2. **Local config file**: `~/.config/overseer/config.json` (`$XDG_CONFIG_HOME/overseer/config.json` when that is set). Set `OVERSEER_CONFIG_FILE` to use another path.
3. **Environment variables** prefixed with `OVERSEER_`, with `__` separating sections.

Other environment variables aren't read as configuration. The only exception is `ASPNETCORE_ENVIRONMENT`, which picks the environment (Production when unset).

| Configuration    | Config file key                   | Environment variable                        | Default                                                                                     |
| ---------------- | --------------------------------- | ------------------------------------------- | ------------------------------------------------------------------------------------------- |
| Database         | `ConnectionStrings:Overseer`      | `OVERSEER_CONNECTIONSTRINGS__OVERSEER`      | none; `serve` exits with a message when it is missing (the compose database in Development) |
| Listen address   | `Urls`                            | `OVERSEER_URLS`                             | `http://localhost:5080`                                                                     |
| Username         | `User:Name`                       | `OVERSEER_USER__NAME`                       | `overseer`                                                                                  |
| Password hash    | `User:PasswordHash`               | `OVERSEER_USER__PASSWORDHASH`               | none; `serve` exits with a message pointing at `reset-password --local`                     |
| Database login   | `Database:EntraAuthentication`    | `OVERSEER_DATABASE__ENTRAAUTHENTICATION`    | `false`: the connection string's password. `true`: a Microsoft Entra token, as in Azure (see [Database login](#database-login)) |
| Database identity | `Database:ManagedIdentityClientId` | `OVERSEER_DATABASE__MANAGEDIDENTITYCLIENTID` | none; the system-assigned identity, or your Azure CLI login                                |
| Sandbox subscription | `SandboxProvider:Azure:SubscriptionId` | `OVERSEER_SANDBOXPROVIDER__AZURE__SUBSCRIPTIONID` | none; without any `SandboxProvider:Azure` setting, the fake Sandbox Provider (see [Sandboxes](#sandboxes)) |
| Sandbox resource group | `SandboxProvider:Azure:ResourceGroup` | `OVERSEER_SANDBOXPROVIDER__AZURE__RESOURCEGROUP` | none; required with the subscription                                                    |
| Private Sandboxes | `SandboxProvider:Azure:PrivateNetwork` | `OVERSEER_SANDBOXPROVIDER__AZURE__PRIVATENETWORK` | `false`: a public IP only you reach. `true`: deployed mode, no public IP (see [Sandboxes](#sandboxes)) |
| Sandbox identity | `SandboxProvider:Azure:ManagedIdentityClientId` | `OVERSEER_SANDBOXPROVIDER__AZURE__MANAGEDIDENTITYCLIENTID` | none; your Azure CLI login. Set: the Azure CLI logs in as that user-assigned Managed Identity |
| Key Vault        | `Secrets:KeyVaultUri`             | `OVERSEER_SECRETS__KEYVAULTURI`             | none; the local secrets file (see [Secrets](#secrets))                                      |
| Managed Identity | `Secrets:ManagedIdentityClientId` | `OVERSEER_SECRETS__MANAGEDIDENTITYCLIENTID` | none; the system-assigned identity, or your Azure CLI login                                 |
| Secrets file     | `Secrets:File`                    | `OVERSEER_SECRETS__FILE`                    | `secrets.json` next to the local config file: `~/.config/overseer/secrets.json`             |

For example, to listen on another port for one run:

```sh
OVERSEER_URLS=http://localhost:5090 dotnet run --project src/Overseer
```

### Database login

With `Database:EntraAuthentication` set to `true`, `serve` logs in to PostgreSQL as the connection string's `Username` with a Microsoft Entra token instead of a password, renewed every 30 minutes. In Azure the token is the Managed Identity's (`Database:ManagedIdentityClientId` names a user-assigned one); locally it is your Azure CLI login's. Install configures this, so the deployed connection string holds no password.

### Sandboxes

Without `SandboxProvider:Azure` settings, `serve` uses the fake Sandbox Provider and logs that at startup. Its Sandboxes exist only in memory, every command succeeds without output, they hold no repository files, and every agent turn is done straight away without handing anything in, save a Designer's, which hands in a placeholder screenshot, so the Portal and orchestration work offline. A started Intent's Refinement therefore fails after three turns, as the Analyst never hands in a Round or a Spec. Tests script it through `FakeSandboxProvider` (command results in sequence, for a whole command or for every command holding a fragment, commands and destructions held until the test lets go, the files each repository has in the Sandboxes that hold it and files that can't be read, agent sessions turn by turn with their updates, also ones a test writes while the turn goes on (`FakeAgentTurn.Live`), outcomes and the files each turn writes in the Sandbox (`FakeAgentTurn.Writes`, such as a Round or Spec handed in, and `BinaryWrites`, such as screenshots), sessions that fail to start, messages that can't be sent (`FakeAgentTurn.SendFailure`), turns that break off or never end, creation and destruction failures, slow provisioning), can write a file in a Sandbox directly (`WriteFile`), as an agent or command would, for `TakeFileAsync` or `TakeBinaryFileAsync` to take, and check what it was asked for (the machine, push access and Repositories in scope of each Sandbox, the GitHub token each was last given since its creation, the commands run in them, the role, agent harness configuration, model and environment variables each agent session started with, the messages sent to agents, and the sessions stopped). The server under test always gets a fake `RunningOverseer` creates, through `OverseerServer.Build`'s `sandboxes`, as it does the GitHub Gateway; since interrupted Refinements continue as `serve` starts, a test scripts it as it is created, before the server uses it, by passing `sandboxes` to `RunningOverseer.StartAsync`.

To work with real Sandboxes from your machine, configure the Azure Sandbox Provider with a subscription ID (`az account show --query id`) and an existing resource group to create Sandboxes in, such as an installation's `<prefix>-sandboxes-rg`:

```json
{ "SandboxProvider": { "Azure": { "SubscriptionId": "<subscription ID>", "ResourceGroup": "<resource group>" } } }
```

`serve` then refuses to start without both, and logs where Sandboxes go. Like the installer, the provider manages Azure only through the Azure CLI (its scripts are in `src/Overseer/Sandboxes/Scripts`), logged in as you, which needs Contributor on the resource group. Each Sandbox is an Ubuntu 24.04 VM `sandbox-<id>` of the size chosen on the Settings page (see [Settings](#settings)), a spot VM with eviction policy Delete if spot instances are chosen there, tagged with its owner (`overseer-owner-kind`, `overseer-owner-id`), with:

- its own network security group, public IP, network interface and 64 GiB OS disk, named after the VM, on the VNet `sandboxes` (10.41.0.0/16) that the provider creates in the resource group when it is missing. The security group lets in SSH only from your current public IPv4 address, as api.ipify.org sees it at creation, and nothing from the other Sandboxes on the VNet. A Sandbox created before your address changed is no longer reachable;
- a fresh SSH key pair. The private key is kept in the secret store as `<vm>-ssh-key`, never in PostgreSQL, so Overseer still reaches the Sandbox after a restart. Overseer also generates the VM's SSH host key, hands it over through cloud-init and tags the VM with its fingerprint (`overseer-host-key`), so it never logs in to another machine;
- Docker with Compose, git, `gh` 2.99 or newer, Node 24 with Playwright and its browsers (in `/opt/ms-playwright`), and OpenCode, installed by cloud-init (`src/Overseer/Sandboxes/Setup/setup-sandbox.sh`). OpenCode is pinned to one exact release, `opencode_version` at the top of that script, as the default `opencode.json` is written for it; setup fails if another version ends up installed. Bump it only together with the default configuration, checked against that release's documentation. Creation takes several minutes: it completes only once cloud-init has finished, and fails with what cloud-init reported if it didn't succeed, or if the Sandbox isn't set up within 30 minutes.

In Azure, install configures deployed mode instead (`PrivateNetwork` and `ManagedIdentityClientId`), so the deployed Overseer needs no configuration of its own. The provider logs the Azure CLI in as the Managed Identity once per process, with an Azure CLI configuration of its own (`AZURE_CONFIG_DIR`), so no other login on the machine is replaced. Sandboxes then differ in their network:

- no public IP: nothing in them is reachable from the internet. Their security group lets in SSH only from Overseer's Container Apps subnet (10.40.0.0/23), which reaches the `sandboxes` VNet over the peering, and Overseer connects to the Sandbox's private IP;
- outbound traffic leaves through the NAT Gateway, from its public IP;
- the provider doesn't create the `sandboxes` VNet, because only install creates it with the NAT Gateway and peering. Without it, creation fails and says to run install again.

The image (`Dockerfile`) has the Azure CLI for this. A creation that fails, with the Azure error or cloud-init's, removes what it created. Commands run over SSH as `overseer`, with standard error merged into standard output. Destroying a Sandbox deletes the VM, its disk, network interface, public IP (if any) and network security group, then its key; the `sandboxes` VNet stays for the next one. Agent sessions in Azure Sandboxes come with the Agent Session module.

Any Sandbox Provider must pass the contract suite in `tests/Overseer.Tests/SandboxProviderContract.cs`: derive from it once per implementation. The fake runs it in the default test run. The Azure provider runs it only when `TEST_SANDBOX_SUBSCRIPTION_ID` and `TEST_SANDBOX_RESOURCE_GROUP` name an existing resource group where your Azure CLI login has Contributor. It creates real Sandboxes one after the other, one of them a spot `Standard_D2_v5` (the subscription needs spot quota for its region), and destroys each one, which takes about an hour and costs a little; its agent session tests are skipped until the Agent Session module exists:

```sh
TEST_SANDBOX_SUBSCRIPTION_ID=$(az account show --query id --output tsv) TEST_SANDBOX_RESOURCE_GROUP=my-sandboxes-rg dotnet test --filter AzureSandboxProviderContract
```

The default test run covers the Azure provider against a fake `az` and a fake SSH (`AzureSandboxProviderTests`), and its SSH client against a real OpenSSH server in a container (`SandboxSshTests`), as is the command that runs a repository's `setup` (`SetupCommandSshTests`).

### Secrets

Secrets, such as the GitHub token and model provider tokens, are write-only: once set, a secret can be replaced, removed, checked for or listed by name, but the Portal never shows its value again. The Portal uses `ISecretStore`, which has no way to read a value; only server-side code, such as injecting a token into a Sandbox, uses `ISecretReader`. A test fails if a Portal component injects the reader. Secret values never go into PostgreSQL or the logs; the logs only say which secret was set or removed. Names are 1 to 127 letters, digits and dashes (Key Vault's rule) and ignore case, whichever backend holds them.

`serve` logs at startup where secrets are kept:

- **Key Vault**, when `Secrets:KeyVaultUri` is set. In Azure, Overseer reaches the vault through its Managed Identity (set `Secrets:ManagedIdentityClientId` for a user-assigned one). Locally, it falls back to your Azure CLI login (`az login`), which needs the Key Vault Secrets Officer role on the vault. Removing a secret soft-deletes it, as Key Vault always does; setting it again recovers it and replaces its value, so this also works with purge protection.
- **The local secrets file** otherwise: `secrets.json` next to the local config file, so `~/.config/overseer/secrets.json` by default, outside any repository. Overseer creates it readable only by you. It holds the values in plain text, so don't move it into a repository. `Secrets:File` puts it elsewhere.

Every backend must pass the contract suite in `tests/Overseer.Tests/SecretStoreContract.cs`. The file backend runs in the default test run. The Key Vault backend runs only when `TEST_KEYVAULT_URI` points at a vault where your Azure CLI login has Key Vault Secrets Officer; each test uses fresh secret names and removes them afterwards:

```sh
TEST_KEYVAULT_URI=https://my-vault.vault.azure.net/ dotnet test --filter KeyVaultSecretStoreContract
```

### GitHub

The GitHub Gateway (`IGitHubGateway`) is the only component that calls the GitHub API, at api.github.com. It reads the GitHub token itself, through `ISecretReader`, and its requests are never logged. Tests use `FakeGitHubGateway` (`tests/Overseer.Tests`), which `RunningOverseer` gives every test server and which tests script to accept or reject tokens, to have repositories, reachable by every token or only one, and to have organizations. It creates repositories for the token's user and in its organizations, or refuses an owner as scripted, and records what it creates. Tests also give its repositories files on a branch, as an agent would push them, and branches, from which it opens pull requests, recording them, or refuses to as scripted; they can close those and delete branches. Both pass the contract suite in `tests/Overseer.Tests/GitHubGatewayContract.cs`. The real gateway runs it only when `TEST_GITHUB_TOKEN` holds a token GitHub accepts and `TEST_GITHUB_LOGIN` the login it works as, such as your GitHub CLI's:

```sh
TEST_GITHUB_TOKEN=$(gh auth token) TEST_GITHUB_LOGIN=$(gh api user --jq .login) dotnet test --filter GitHubGatewayContractAgainstGitHub
```

The contract creates private repositories named `overseer-contract-<id>` for that login, and deletes them after each test. It also checks that creating one for `octocat`, another user, fails; creating one in an organization is not covered. It checks for files on a branch in octocat/Hello-World and in a new, empty repository. To open pull requests, it commits to a repository it created, branches it, and opens one from the branch; to look for them, it also closes one and deletes its branch. That needs a fine-grained token with "Administration: Read and write", "Contents: Read and write" and "Pull requests: Read and write" on all of your repositories, or a GitHub CLI login with the `delete_repo` scope (`gh auth refresh -s delete_repo`). A repository it could not delete fails the test, naming it, so you can delete it by hand.

## Tests

```sh
dotnet test
```

Docker must be running: the server tests start Overseer in-process against a real PostgreSQL through Testcontainers. The test of the command that puts a repository on the Intent branch runs it against local repositories, so it needs bash and git.

The browser tests (`LiveIntentsBrowserTests`) drive the Portal's live updates in a headless Chromium through Playwright. They install Chromium themselves into Playwright's cache in your home directory on their first run, so that run needs internet access and takes a little longer. On Linux, Chromium also needs system libraries: after a build, run `pwsh tests/Overseer.Tests/bin/Debug/net10.0/playwright.ps1 install-deps chromium`, which takes root. The release workflow does.

The install, uninstall and Azure reset-password tests run `overseer install`, `overseer uninstall` and `overseer reset-password` against a fake `az` (`tests/Overseer.Tests/FakeAzureCli.cs`), so they need bash but no Azure. An opt-in smoke test installs for real into the subscription your Azure CLI is logged in to, with a fresh prefix, logs in to the Portal, resets the password (after which only the new one logs in), runs install again (which must keep the password and create no new revision), uninstalls, installs again with the same prefix (which must succeed and log in) and uninstalls again. It deletes whatever a failure left behind (about an hour and a half, and a little cost). It needs a released version whose image is published, such as a prerelease tag pushed for the purpose:

```sh
TEST_INSTALL_VERSION=0.2.0-rc.1 dotnet test --filter InstallSmokeTest
```

`TEST_INSTALL_REGION` overrides its region (westeurope).

Another opt-in smoke test, with the same variables, has a deployed Overseer create a Sandbox: it installs, checks that the Managed Identity's only role assignment is Contributor on the Sandbox resource group and that the VNets are peered both ways, and enables the smoke-test endpoints. Through them, as the logged-in user, the deployed Overseer creates a Sandbox. The test checks that the Sandbox has no public IP and lets in SSH only from Overseer's subnet, runs a command in it that shows it reaches the internet from the NAT Gateway's IP, destroys it, and uninstalls (about an hour):

```sh
TEST_INSTALL_VERSION=0.2.0-rc.1 dotnet test --filter DeployedSandboxSmokeTest
```

The smoke-test endpoints (`/smoke-test/sandboxes`) exist only when `SmokeTest:Enabled` (`OVERSEER_SMOKETEST__ENABLED`) is `true`. Install never sets it; the test sets it on the Container App with `az containerapp update`. They only create, run commands in and destroy the logged-in user's Sandboxes, in the background, because creating one takes longer than Container Apps lets a request last.

## Releases

Pushing a tag `vX.Y.Z` (optionally with a prerelease suffix, such as `v1.2.0-rc.1`) runs `.github/workflows/release.yml`. After the tests pass, it builds every release output with the version X.Y.Z, so all of them report it:

- **CLI**: `overseer-X.Y.Z-<rid>.tar.gz` for `linux-x64`, `linux-arm64`, `osx-x64` and `osx-arm64`, attached to the GitHub release. Unpack the whole archive: `serve` needs the files next to the executable.
- **Container image**: `ghcr.io/pnieuwenhuis/overseer:X.Y.Z` (linux/amd64), built from the `Dockerfile` with `--build-arg VERSION=X.Y.Z`. It runs `overseer serve` on port 8080 by default and needs `OVERSEER_CONNECTIONSTRINGS__OVERSEER` and `OVERSEER_USER__PASSWORDHASH`. It includes the Azure CLI, pinned in the `Dockerfile`, for the Azure Sandbox Provider. The workflow smoke-tests the image against a throwaway PostgreSQL before pushing it, and checks that the Portal shows the tag's version and that the Azure CLI runs.

The Portal header and `overseer --version` show that version. A local build reports `0.0.0-dev` instead, which tells it apart from any release.

To build and smoke-test an image locally (Docker must be running):

```sh
docker build --build-arg VERSION=0.0.1-local --tag pnieuwenhuis/overseer:0.0.1-local .
scripts/smoke-test-image.sh pnieuwenhuis/overseer:0.0.1-local
```

The repository and the `overseer` package on GHCR are public, so the CLI can be downloaded and the image pulled anonymously.

## Schema changes

The schema is defined by the EF Core model in `src/Overseer/Persistence`. After changing it, add a migration with the repository's local `dotnet-ef` tool:

```sh
dotnet tool restore
dotnet dotnet-ef migrations add <Name> --project src/Overseer --output-dir Persistence/Migrations
```
