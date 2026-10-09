---
name: Bug report
about: Report a problem so we can fix it
title: "[Bug] "
labels: ["bug"]
assignees: []
---

<!-- Thanks for taking the time to file a bug! Please fill in the fields below. -->

## Description

A clear and concise description of what the bug is.

## Steps to reproduce

1. Go to '...'
2. Click on '...'
3. See error

## Expected behaviour

What you expected to happen.

## Actual behaviour

What actually happened. Include screenshots or a short screen recording if it helps.

## Environment

| Field | Value |
|---|---|
| App version | e.g. 3.1.0 |
| Windows version | e.g. Windows 11 23H2 |
| Display scaling | e.g. 125 % (2K) |
| Theme | Light / Dark / Follow system |
| Language | English / 中文 |
| Camera / source | Local USB / Network (RTSP/MJPEG) / File replay |
| Stream path (network only) | e.g. `/cam1` — a wrong path gives a 404 that only shows as "cannot connect" |
| Stream resolution | e.g. 1280x720 |
| Processing mode | e.g. Canny / Color detection / … |
| Production-line card | Disabled / Enabled, and whether an ROI or MES gateway is configured |

## Does it hang?

If the app freezes rather than erroring, please say which:

- [ ] Window stops responding, but the process is alive
- [ ] Window disappears, the process stays in Task Manager
- [ ] A modal error dialog appears
- [ ] A crash dialog with a stack trace appears

Roughly how long did you wait? Also: does it happen **every** time or only sometimes?

> A freeze that is sometimes fine and sometimes not is usually a race between threads.
> Whether it reproduces reliably is the single most useful thing you can tell us.

## Logs

Relevant log lines from the in-app **Log** page (redact any secrets).

```
paste here
```

## Additional context

Anything else that might be relevant.
