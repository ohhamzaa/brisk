# Security Policy

Brisk ends processes, deletes junk files and changes process priorities, so safety bugs matter to us.

## Reporting a vulnerability

Please **don't open a public issue** for security problems. Use GitHub's private
[**Report a vulnerability**](https://github.com/ohhamzaa/brisk/security/advisories/new) form instead.

Especially useful reports:

- The cleaner deleting anything outside the folders it lists, or following a link or junction
- A way to make Brisk end or throttle a protected Windows process
- Game boost leaving apps throttled, or the power plan changed, after it should have restored them
- Brisk sending anything over the network besides the optional router / 1.1.1.1 ping

You'll get a reply within a few days. Fixes ship in a patch release, with credit if you'd like it.

## Supported versions

Only the latest release gets security fixes.

## Verifying downloads

Every release binary is built by GitHub Actions from the tagged commit; see the
[build workflow](.github/workflows/build.yml). Releases aren't code-signed yet, so Windows
SmartScreen may show a warning the first time you run Brisk.
