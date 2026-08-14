# Security Policy

## Supported versions

Security fixes are provided for the latest source revision and the latest published release.

## Reporting a vulnerability

Please use GitHub private vulnerability reporting when it is enabled for the repository. Otherwise, open a minimal issue asking for a private contact channel; do not include credentials, private task text, SSH configuration, local log files or exploit details in a public issue.

Useful reports include:

- the affected version and Windows version;
- the smallest reproducible sequence;
- whether the issue involves local session files, named-pipe IPC, SSH or UI Automation;
- redacted logs containing no task text, usernames, hostnames, paths, tokens or credentials.

## Security boundaries

This application reads data belonging to the current Windows user and invokes existing local Codex and OpenSSH facilities. It is not a security boundary and should not be run with elevated privileges. The build manifest requests normal `asInvoker` execution.
