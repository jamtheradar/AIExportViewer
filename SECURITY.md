# Security

AI Export Viewer is built to open highly sensitive data, so security and privacy reports are welcome.

## Reporting a problem

Please report it privately through GitHub: **Security → Report a vulnerability** on this repository. Don't open a
public issue, and never attach real export data, names, emails or screenshots of real conversations. A made-up example
is enough.

Useful things to include:

- what you did and what happened
- the app version (Settings → Updates)
- whether it involves the network, local storage, or content shown from an export (chat text, artifacts, attachments)

## What the app promises

- It works offline. It only connects to the internet to download an export from claude.ai when you ask, and to check
  GitHub for a newer version of the app (this can be turned off in Settings).
- Export files are only ever read, never changed.
- Everything shown from an export is treated as untrusted: HTML is sanitised and artifact code never runs.
- Everything it stores stays on the computer, in one folder, and Settings can delete it all.

Anything that breaks one of these is a security problem.
