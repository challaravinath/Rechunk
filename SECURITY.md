# Security

## Supported versions

Fixes go into the latest release. Rechunk is below 1.0, so older minor versions do not get patches.

## Reporting a problem

Please do not open a public issue for a security problem. Use the private report form on GitHub instead: the Security tab of this repo, then "Report a vulnerability".

Include what you found, the version, and steps to reproduce it if you have them. I will reply within a week, and keep you posted until it is fixed. If you want credit in the release notes, say so in the report.

## What counts

Anything that leaks chunk text, PII findings, keys or erased data, or lets one tenant or scope read or erase another's data. Also anything that makes an erasure receipt claim something was removed when it was not.
