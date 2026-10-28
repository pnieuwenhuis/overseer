# 02: Versioned release: executable and container image share one version

**What to build:** Every release produces an `overseer` executable and a container image that carry exactly the same version, with the image published to a public container registry. This is what lets `install` deploy the image tag matching its own version, so the CLI and the server never drift apart. Covers user story 5 (the release side; the deploy side is in 06 and 07).

**Blocked by:** 01 (Walking skeleton)

**Status:** ready-for-agent

- [ ] `overseer --version` prints the version stamped in at build time
- [ ] The release build stamps one version into both the executable and the container image tag
- [ ] The container image runs `overseer serve` by default
- [ ] The image is published to a public container registry and can be pulled anonymously
- [ ] The CLI is published as release artifacts for at least macOS and Linux
- [ ] The Portal shows the same version as the image tag it runs from
- [ ] A local development build reports a recognisable non-release version, so `install` can tell it apart from a release
