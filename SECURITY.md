# Security Policy

Soforr Business OS handles businesses' sales, stock, customer balances and money records, so security reports are taken seriously.

## Reporting a vulnerability

**Please report vulnerabilities privately. Do not open a public issue or pull request for something exploitable.**

Use GitHub's private vulnerability reporting: open the repository's **Security** tab and choose **Report a vulnerability**
(https://github.com/Ulaysu/soforr-business-os/security/advisories/new). This creates a private advisory that only the maintainers can see.

Please include:

- what you found and where (endpoint, file, or workflow),
- the steps or a request that reproduces it,
- what an attacker could do with it, and
- whether it involves another organization's data (tenant isolation), authentication, money or stock records.

Do not include real customer data, and please do not test against systems you do not own.

## What to expect

This is a small project, so these are goals, not contractual promises:

- an acknowledgement within 5 working days,
- an assessment and a plan, or a request for more detail, within 15 working days,
- a fix, or a documented mitigation, for confirmed high-severity issues as quickly as it can be done safely, and credit in the advisory if you want it.

## Scope

In scope: the API and web application in this repository, the authentication and session handling, tenant isolation, authorization,
the offline synchronization contract, the build and CI configuration, and the dependencies it ships.

Out of scope: social engineering, denial of service by sheer volume, findings that need a compromised device or browser profile
(local offline data is not encrypted by design; see [the security principles](docs/security/security.md)), and issues in third-party
services we do not operate.

## Supported versions

There is no released version yet. Fixes are made on `main`.

## For contributors

- Never commit secrets. A secret scan runs in CI, and GitHub secret scanning with push protection should be enabled on the repository.
- Dependencies are watched by Dependabot, and CI fails on known high or critical vulnerabilities in NuGet and npm packages.
- Security-relevant changes are described in the pull request ([template](.github/pull_request_template.md)).
- If you suspect a secret was committed, rotate it first, then tell a maintainer; removing it from history is not enough.

The principles and the threat model are in [docs/security](docs/security/).
