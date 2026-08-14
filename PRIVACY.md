# Privacy

Codex Deck is a local Windows desktop application. It has no telemetry, analytics service, advertising SDK, account system, or project-operated cloud service.

## Data read by the application

To present task status, the application may read:

- local Codex session records under the current user's `.codex` directory;
- Codex Desktop global state and local diagnostic logs;
- task identifiers, titles, working directories, status flags and short task previews;
- state exposed by the local Codex Desktop named-pipe IPC interface;
- equivalent session information from Codex-managed remote hosts configured on the machine, using the existing local SSH configuration.

Because task previews are displayed, limited user-authored task text may be processed in memory and in the application's local state. Do not use the application on a machine where other local users may access its profile data unless that is acceptable for your environment.

## Data written by the application

Application state and diagnostics are stored under:

```text
%LOCALAPPDATA%\CodexProjectCenter
```

This may include task identifiers, cached titles, acknowledgement state, navigation state and performance diagnostics. It should not be attached to public bug reports without review.

## Network and remote access

The application does not upload task data to a service operated by this project. When remote Codex projects are configured, it can invoke the system OpenSSH client to read task information from those configured hosts. Codex Desktop itself may independently use network services under its own terms and privacy policy.

## Removing local data

Exit the application and delete `%LOCALAPPDATA%\CodexProjectCenter` to remove its cached state and diagnostics. This does not delete Codex conversations or Codex Desktop data.
