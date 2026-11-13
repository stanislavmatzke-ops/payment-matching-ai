# Security policy

This is a reference project with a public demo at https://payments.stanislavmatzke.com. All data in it
is synthetic (fictional companies, the fictional "Demo Bank"); no real payments or accounts are
processed.

## Reporting a vulnerability

Please report security issues privately, not in a public issue:

- through GitHub: **Security → Report a vulnerability** in this repository, or
- by e-mail to kontakt@stanislavmatzke.com.

Describe what you found and how to reproduce it. You will get an answer within a few days. Please do
not test against the live app in ways that could affect other people's sandboxes (no load testing, no
automated scanning of other accounts).

## Scope

In scope: the code in this repository and the live app – for example tenant isolation (seeing or
changing another person's sandbox), getting the app to call a model or to send your own text to one,
prompt injection that leads to an automatic match, leaks of statement data into telemetry, or problems
in the deployment workflow.

How the project defends against these is described in [docs/security.md](docs/security.md).

## Supported versions

Only the current `main` branch, which is what the live app runs.
