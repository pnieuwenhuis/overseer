# 01: Log out and account lockout

**What to build:** The Portal header has a "Log out" action that ends the session and returns to the login page. Repeated failed logins lock the account temporarily, so the public Portal can't be brute-forced. After 5 consecutive failed logins, every login attempt is refused for 15 minutes, even one with the correct password. The login page says the account is temporarily locked and until when. Covers user story 16 and completes story 15 (the login itself was delivered by ticket 03 of the install-and-operate tickets).

Decided: the lockout state lives in memory, not in PostgreSQL. Overseer runs as a single replica, so a restart resetting the count is acceptable. Locking out a single account also lets an attacker keep the real user out for a while; the spec accepts that by making the lockout temporary.

**Blocked by:** None (can start immediately)

**Status:** done

- [x] The Portal header offers "Log out"; afterwards every Portal page requires logging in again
- [x] 5 consecutive failed logins lock the account for 15 minutes; during the lockout a correct password is refused as well
- [x] While locked, the login page says the account is temporarily locked and when it unlocks, without saying whether the submitted credentials were right
- [x] A successful login resets the failure count; the lockout ends by itself after its period
- [x] A wrong username counts as a failed login, the same as a wrong password
- [x] Lockout timing comes from the injected `TimeProvider`, so tests control the clock instead of waiting
- [x] Covered by tests through the application (`RunningOverseer`), including unlocking after the period

Not covered: log out deletes the cookie in the browser that logs out, but sessions stay stateless, so a Portal tab already open elsewhere keeps its live connection until it reloads, and a copied cookie stays valid until it expires. Ending those too needs server-side session state.
