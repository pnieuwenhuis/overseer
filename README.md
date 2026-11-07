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

An Intent's light says at a glance how it is doing: green with the label Working while agents work on it, and red with the label Failed when it has failed and waits for you, so far when its Refinement failed (see below). The label makes it readable without the colour. States in which nothing runs show no light. `IntentLifecycle.LightOf` derives it in one place, which is where orange (Needs Attention) goes when Overseer gains what sets it off; for now only a Refining Intent can have a light.

Every Intent but a Draft shows its Working Time, in whole hours and minutes ("2 h 15 min"): the total time it has spent in the Active states. Time as a Draft, Awaiting Spec Approval, Approved or Paused doesn't count. `IntentLifecycle` records every transition it makes, with its time from the injected `TimeProvider`, in the `IntentTransitions` table, and derives Working Time from that history and nothing else. A transition changes the state only through `IntentLifecycle.TransitionAsync`, which records it. The Intents captured before the history was recorded count their Working Time from that upgrade: the first `serve` after it records their start at its own time, once the migrations are applied.

The Backlog and the detail page update live, over Blazor's SignalR connection: `IntentLifecycle` and `Refinements` announce each change once it is committed, and the page reads again. A detail page stops offering Start, Edit and Delete as soon as the Intent is no longer a Draft, and Start goes disabled, with the reason, as soon as the last slot is taken. The Working Time of an Active Intent advances as each minute passes, without anything announced. A change in Settings shows when the page is next opened or an Intent next changes.

New Intent opens an editor for the Intent's text, in free Markdown. Preview renders what is in the editor without saving it, the same way as the Review Guidelines (see [Settings](#settings)). Save as Draft stores the Intent in PostgreSQL, with LF line breaks, and returns to the Backlog; text with nothing but whitespace is refused. A Draft is only captured: no agent works on it.

The page also offers every Configured Repository as a Repository in scope, all selected when it opens. Preview and a refused save keep your selection. They are stored with the Intent, as the repositories are named at that moment, and the Backlog shows them. A Draft can have none, so the Backlog can be filled before any repository is configured. When a selected repository was removed after the page opened, Preview and any save say so, naming it, and a save is refused rather than silently saving a smaller scope: the page then offers the current repositories and keeps your text and the rest of your selection, so saving again goes on without it.

Clicking an Intent in the Backlog opens its detail page, with its rendered text, state and Repositories in scope. An address that names no Intent, or a malformed one, gets the Not Found page.

While the Intent is a Draft, the detail page offers Edit and Delete. Edit opens the same editor as New Intent, starting from the stored text and Repositories in scope, and refuses and keeps what you entered in the same cases. Delete opens a page that asks for confirmation, and only its button deletes. The Intent is then gone from the Backlog and from PostgreSQL, with its Repositories in scope, so they no longer hold those Configured Repositories back from removal; it isn't Cancelled.

`IntentLifecycle` decides on an edit or delete under a lock on the Intent's row, so what counts is the Intent's state at the moment of writing, not when the page was opened. Anything but a Draft is refused, and so is an Intent that was deleted in the meantime; an editor keeps your text so that it isn't lost. A change of state has to update that row, so that an edit or delete under way and the change wait for each other.

A Draft's detail page also offers Start, which moves it to Refining: the only way out of Draft besides deleting. From then on the Intent's text and Repositories in scope can't be edited, and it can't be deleted. The server refuses that as above, whatever a request says, and it refuses a Start from any state that Start doesn't move an Intent on from.

Start is disabled, with the reason below it, while the Draft has no Repository in scope or all Active Intent slots are taken. The server refuses with the same explanation, which says which Intents take the slots and that you can raise the maximum in Settings. An Intent takes a slot in the Active states, Refining, Implementing and In Review, and `IntentLifecycle` defines them in one place; a Draft or a Repository Setup never takes one. Lowering the maximum below the number of Active Intents leaves them as they are: Start simply stays disabled until a slot is free.

A Start holds a PostgreSQL advisory lock from before it counts the Active Intents until it commits, after locking the Intent's row. Under READ COMMITTED a count doesn't see a slot another transaction has taken but not committed, so without the lock two Starts could both take the last one. Any later transition into an Active state has to take the lock the same way.

### Refinement

Starting an Intent launches its Refinement: once the move to Refining is committed, `IntentLifecycle` raises `Transitioned`, its hook for the side effects of transitions, and `Refinements` works on the Intent in the background. It never sets the Intent's state, and the lifecycle knows nothing of it: a failed Refinement turns the light red through `IIntentFailures`, which `Refinements` implements and `IntentLifecycle.LightOf` reads. It creates a Sandbox owned by the Intent, holding exactly its Repositories in scope, on the machine the Settings say, and once that is ready starts an Analyst agent session there, with the current agent harness configuration and model provider tokens, like any agent session. The Analyst's first message holds its instructions, the Intent's text and its Repositories in scope. The instructions allow fetching web pages over HTTP (OpenCode's `webfetch` tool, or `curl` in the shell: there is no web search service), reading the code, and building and running the tests in the Sandbox, and forbid pushing. Overseer's default `opencode.json` allows every tool without asking and denies the Analyst `git push` and `git push *`: OpenCode applies the last bash rule that matches, and an agent's rules over the global ones. That only catches a push written that way, and you can edit the configuration, so it is no guarantee.

The detail page of a Refining Intent shows where its Refinement stands, and updates live: provisioning the Sandbox, which takes minutes in Azure, then the Analyst working, both with a green light. `Refinements` keeps that phase in the `Refinements` table, and runs in the background through `SandboxedWork`, as Repository Setups do. The Analyst can't yet hand in a Round or a Spec, so every outcome its turn ends with fails the Refinement, as a broken-off session does.

When the Sandbox can't be created, or the Analyst's session fails, the Intent stays Refining and keeps its slot, its light turns red, and the detail page shows the error (the provider's own, for a Sandbox) and a Retry button. Retry launches the Analyst again in a fresh Sandbox, turning the light green again. A Refinement's Sandbox is destroyed as soon as it fails. When `serve` stops during a Refinement, its Sandbox is destroyed, and the next start marks the Refinement failed so it can be retried, and destroys any Sandbox an Intent left behind, before the Portal takes requests. A Sandbox is recorded before the provider is asked to create it, so one a crash interrupts is found too, and one whose destruction fails stays recorded until a later start destroys it. Resuming from the transcript after a restart replaces this with the Refinement idle timeout (ticket 05).

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

The agent harness configuration is the `opencode.json` every agent session starts with. Each role has an OpenCode agent named after it (`analyst`, `designer`, `developer`, `reviewer`), and each of those agents must set a `model`, as OpenCode names it: `provider/model`. Until you save your own configuration, Overseer's default applies, with Claude Opus for the Analyst and Reviewer and Claude Sonnet for the Designer and Developer. Edit the JSON as a whole, or pick a model per role: the picker changes that role's `model` in the JSON and keeps everything else, though it rewrites the JSON indented by two spaces when a model changes. Saving rejects anything other than a JSON object, with the JSON parser's message (which counts lines from 0), and any configuration that lacks a model for a role, naming the role. Nothing is saved then. Restore the default to go back to Overseer's, including later versions' changes to it. Orchestration starts agent sessions through `SandboxRegistry.StartAgentSessionAsync`, which hands the Sandbox Provider the configuration saved at that moment, so a change applies to sessions started after it and never to running ones.

Model provider tokens are the keys for the model providers in `opencode.json`, such as Azure Foundry or an AI gateway, each stored under a name: 1 to 64 letters, digits and dashes, starting with a letter, ignoring case. They are secrets (see [Secrets](#secrets)), each in the secret `model-provider-token-<name>`: the Settings page lists them by name, never by value, and adds, replaces (saving under a stored name) and removes them. Every agent session gets every token stored at its start as an environment variable: the name in capitals, dashes as underscores, followed by `_API_KEY`, so `azure-foundry` gives `AZURE_FOUNDRY_API_KEY` and `anthropic` gives `ANTHROPIC_API_KEY`, the variable that provider reads by default. Reference one in `opencode.json` as `{env:AZURE_FOUNDRY_API_KEY}`, as OpenCode substitutes it. To switch provider, add its token and edit the configuration; Overseer needs no change. A configuration that references a variable no stored token provides is still saved, but the Agents section warns, naming the variable, for as long as that is so.

The GitHub token is the fine-grained token Overseer and its agents work with GitHub as. It is a secret (see [Secrets](#secrets)): the Settings page says whether one is set and lets you check it with GitHub, which says the login it works as or why it fails, and lets you set, replace and remove it, but never shows it. Saving a token first checks it with GitHub. A token that works replaces the one in use; one that fails is kept, in memory only, until you store it anyway or discard it, and until then the token in use stays. A restart forgets it.

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

Without `SandboxProvider:Azure` settings, `serve` uses the fake Sandbox Provider and logs that at startup. Its Sandboxes exist only in memory, every command succeeds without output, and every agent turn is done straight away, so the Portal and orchestration work offline. A started Intent's Refinement therefore fails at once, as the Analyst hands in nothing Overseer can act on yet. Tests script it through `FakeSandboxProvider` (command results in sequence, agent sessions turn by turn with their updates and outcomes, sessions that fail to start, turns that break off or never end, creation and destruction failures, slow provisioning) and check what it was asked for (the machine and Repositories in scope of each Sandbox, the role, agent harness configuration, model and environment variables each agent session started with, and the messages sent to agents).

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

The default test run covers the Azure provider against a fake `az` and a fake SSH (`AzureSandboxProviderTests`), and its SSH client against a real OpenSSH server in a container (`SandboxSshTests`).

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

The GitHub Gateway (`IGitHubGateway`) is the only component that calls the GitHub API, at api.github.com. It reads the GitHub token itself, through `ISecretReader`, and its requests are never logged. Tests use `FakeGitHubGateway` (`tests/Overseer.Tests`), which `RunningOverseer` gives every test server and which tests script to accept or reject tokens, to have repositories, reachable by every token or only one, and to have organizations. It creates repositories for the token's user and in its organizations, or refuses an owner as scripted, and records what it creates. Tests also give its repositories files on a branch, as an agent would push them. Both pass the contract suite in `tests/Overseer.Tests/GitHubGatewayContract.cs`. The real gateway runs it only when `TEST_GITHUB_TOKEN` holds a token GitHub accepts and `TEST_GITHUB_LOGIN` the login it works as, such as your GitHub CLI's:

```sh
TEST_GITHUB_TOKEN=$(gh auth token) TEST_GITHUB_LOGIN=$(gh api user --jq .login) dotnet test --filter GitHubGatewayContractAgainstGitHub
```

The contract creates private repositories named `overseer-contract-<id>` for that login, and deletes them after each test. It also checks that creating one for `octocat`, another user, fails; creating one in an organization is not covered. It checks for files on a branch in octocat/Hello-World and in a new, empty repository. That needs a fine-grained token with "Administration: Read and write" and "Contents: Read" on all of your repositories, or a GitHub CLI login with the `delete_repo` scope (`gh auth refresh -s delete_repo`). A repository it could not delete fails the test, naming it, so you can delete it by hand.

## Tests

```sh
dotnet test
```

Docker must be running: the server tests start Overseer in-process against a real PostgreSQL through Testcontainers.

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
