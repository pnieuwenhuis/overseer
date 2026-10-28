# The CLI and the server are one executable

Overseer ships as a single `overseer` executable. Commands like `install`, `uninstall` and `reset-password` manage the Azure deployment, and `serve` runs the Portal and the orchestration. The container image runs `overseer serve`, the same command a developer can run locally against a local PostgreSQL. We chose this over separate CLI and web projects so that the deployed version and the CLI version can never drift apart (`install` deploys the image tag matching its own version), and so the whole system can run outside Azure with only the Sandboxes remaining in the cloud.

## Consequences

- When run locally, Overseer gives Sandboxes a public IP that only the developer's current IP can reach. The deployed Overseer reaches them over a private VNet.
- A fake Sandbox provider allows offline work on the Portal and orchestration.
