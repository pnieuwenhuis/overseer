# Overseer

Overseer is a software factory where one human directs a team of AI agents: the human captures what they want as Intents, and agents refine, build, test and review them into merged code.

## Language

**Overseer**:
The whole system: the deployed factory the human uses to direct AI agents.
_Avoid_: Center, factory, web application

**Portal**:
The web interface through which the human uses Overseer.
_Avoid_: Web application, web interface, dashboard (for the whole UI)

### Intents

**Intent**:
A single desired change to the software, described by the human in free text; the smallest unit that ends up merged, producing one Spec and at most one branch and pull request per Repository in scope (none for a repository left unchanged). Its text can only be edited while it is a Draft.
_Avoid_: Ticket, story, feature request, epic

**Backlog**:
All open Intents: those not yet Done or Cancelled.
_Avoid_: Queue, todo list

**Configured Repository**:
A GitHub repository that is part of the software Overseer builds, registered in Overseer's settings. It can't be removed while an open Intent has it in scope.
_Avoid_: Project, codebase

**Repository Manifest**:
A repository's own machine-readable declaration of how to build it, test it and start its dependencies, versioned alongside its code.
_Avoid_: Repo config, settings

**Repository Setup**:
The creation of a new Configured Repository from a stated purpose and tech stack, ending with its starter README, agent guidance and Repository Manifest on main; it is not an Intent and takes no concurrency slot.
_Avoid_: Scaffolding intent, repo bootstrap

**Repository in scope**:
A Configured Repository selected on an Intent as one the Intent may change; by default every configured repository.
_Avoid_: Target repo, workspace repo

**Active Intent**:
An Intent on which agents are currently working (Refining, Implementing or In Review), occupying one of the limited concurrency slots; the maximum is a setting (default 2).
_Avoid_: Running intent, in-progress intent

**Needs Attention**:
The condition of an Active Intent that cannot progress until the human acts, such as answering the Analyst, approving a Spec, or triaging review comments.
_Avoid_: Blocked, waiting, pending

**Working Time**:
The total time an Intent has spent as an Active Intent; time spent Paused or awaiting approval is excluded.
_Avoid_: Elapsed time, duration, age

### Agents

**Analyst**:
The agent role that conducts Refinement and writes the Spec; it exists only during Refinement.
_Avoid_: Refiner, product owner, interviewer

**Designer**:
The agent role that works out the UI and UX for a Spec with UI changes, before Developers build it.
_Avoid_: UX agent, architect

**Developer**:
The agent role that implements Tasks, including the unit and integration tests for its own work.
_Avoid_: Coder, engineer, builder

**Reviewer**:
The agent role that reviews an Intent's pull requests against the Approved Spec and the Review Guidelines, and exercises the running application to verify it.
_Avoid_: Code reviewer, QA agent, tester

**Squad**:
The succession of agents that carry out the Tasks of one Intent's Approved Spec, one Task at a time.
_Avoid_: Team, crew

**Sandbox**:
A temporary, isolated machine holding the Repositories in scope, in which agents work on an Intent. Refinement gets its own; implementation and review share one for the rest of the Intent's life, until it is Paused, Cancelled or Done.
_Avoid_: VM, workspace, environment, container

### Lifecycle

**Draft**:
An Intent that has been captured but on which no agent work has started.

**Refinement**:
The conversation in which the Analyst questions the human about an Intent until it has enough information to write a Spec; only the Analyst decides when that point is reached.
_Avoid_: Grilling, elaboration, specification phase

**Round**:
One batch of numbered questions the Analyst puts to the human during Refinement, each with the Analyst's suggested answer.
_Avoid_: Iteration, turn, questionnaire

**Spec**:
The agent-written, versioned description of how an Intent will be implemented, divided into Tasks; kept separate from the Intent's original text.
_Avoid_: Plan, design doc, requirements

**Task**:
A unit of work in a Spec, assigned to one agent role and touching one or more Repositories in scope. A Spec's Tasks form an ordered list and are carried out one at a time. A finished Task is never reopened; later changes to its work are new Tasks.
_Avoid_: Ticket, job, step, subtask

**Failed Task**:
A Task whose work still does not pass verification after the allowed number of attempts; it stops the Intent until the human intervenes.
_Avoid_: Broken task, errored task

**Analyst's Choice**:
A decision in a Spec that the human left to the Analyst by accepting its suggested answer instead of giving their own.
_Avoid_: Assumption, default

**Approved Spec**:
The Spec version the human has accepted; the contract Tasks are created from and review is held against.

**Approved**:
An Intent whose Spec has been approved but whose implementation the human has not yet started; it holds no concurrency slot.
_Avoid_: Ready, queued, scheduled

**Revision**:
Sending a Paused Intent back to Refinement to produce a new Approved Spec, keeping the work already done.
_Avoid_: Re-spec, amendment

**Paused**:
An Intent whose work has been stopped with the intention of resuming it later; it holds no Sandboxes and no concurrency slot.
_Avoid_: Suspended, on hold

**Cancelled**:
An Intent whose work has been stopped for good; it will never be resumed.
_Avoid_: Abandoned, deleted, rejected

### Review

**Review Guidelines**:
The human's global rules for what a Reviewer must check, maintained in Overseer's settings; repository-specific guidance lives in the repository itself.
_Avoid_: Coding standards, review checklist

**Review Comment**:
A remark on an Intent's pull request, made by the Reviewer or the human, that the human either accepts for processing or dismisses with a note. Comments the human writes are accepted automatically.
_Avoid_: Finding, issue, remark

**Done**:
An Intent that the human has approved and whose pull requests have all been merged to main.
_Avoid_: Completed, finished, closed
