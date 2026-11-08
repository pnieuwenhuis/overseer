# 04: Repository Manifest and test dependencies in Refinement

**What to build:** Refinement makes sure the Squad will be able to verify its work. Before the Analyst's first turn, Overseer uses the GitHub Gateway to check which Repositories in scope have no Repository Manifest (`.overseer.yml`) on their default branch. The Analyst's first message names those repositories. A Spec is rejected, with the reason fed back to the Analyst, unless it contains a Task touching each such repository that creates its Repository Manifest. The Analyst's instructions also tell it to ask the user, in a Round, whether external dependencies such as a database are needed for testing. When they are needed and the repository has no Docker Compose setup for them, the Analyst adds a Task to create one and to reference it from the Repository Manifest. Covers user stories 49 and 50.

Following ADR 0003, Overseer only checks whether the manifest exists. It doesn't parse it here. Parsing and validating it belongs to the Repository Manifest module, which comes with implementation.

**Blocked by:** 03 (The Analyst writes the Spec)

**Status:** done

- [x] Overseer checks each Repository in scope for `.overseer.yml` on its default branch through the GitHub Gateway when Refinement starts or resumes
- [x] The Analyst's first message names the Repositories in scope that lack a Repository Manifest, or says that all have one
- [x] A Spec without a manifest-creating Task for each repository that lacks one is rejected, naming the repositories, and the Analyst gets that reason
- [x] When the Gateway can't tell (no token, or GitHub fails), Refinement still starts; the Analyst is told the check failed and to look for the manifest in its Sandbox itself
- [x] The Analyst's instructions cover asking about external test dependencies and adding a Docker Compose Task when they are needed and missing
- [x] Covered by tests through the application with the fake Sandbox Provider and the fake GitHub Gateway
