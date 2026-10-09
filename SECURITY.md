# Security Policy

## Supported Versions

| Version | Supported |
|---|---|
| 3.x (current) | :white_check_mark: |
| < 3.0 | :x: |

## Reporting a Vulnerability

**Please do not open a public issue for security problems.**

Report suspected vulnerabilities privately via GitHub's
[Security Advisories](https://github.com/virtual-path/virtual-path-vision/security/advisories/new)
(*Security* → *Report a vulnerability*), or by contacting the maintainers directly.

Please include:

- A description of the issue and its impact
- Steps to reproduce (or a proof-of-concept)
- Affected version / commit
- Any suggested mitigation

You can expect an acknowledgement within a few days. Once a fix is available we
will coordinate disclosure and credit you (unless you prefer to stay anonymous).

## Scope Notes

This desktop application can hold credentials for third-party services (AWS,
OPC UA, Modbus, serial scanners). When reporting, please **redact** any real
keys, tokens, or endpoints from logs and screenshots.

Runtime data such as `user_settings.json` (language / theme / window state) is
stored next to the executable and contains no secrets.

### MES gateway token

The production-line card accepts a JWT for the MES gateway (see the
[protocol notes](README.md#mes-gateway-contract)). How it is handled:

- Entered through a password box — not a plain text box
- **Never** written to disk, `user_settings.json`, or the application log
- Held as plaintext in the process for the lifetime of the orchestration layer

The plaintext-in-memory part is a known limitation, not an oversight: the token
is supplied at runtime and there is no OS credential store integration. Do not
paste a production token into a bug report.

### Network stream source

A "Network Stream" source accepts an arbitrary host, port, and path and fetches
it with OpenCV. Treat the URL as untrusted input — there is no allow-list, and
the path segment previously had to be typed correctly by hand (a wrong path
produces a 404 that surfaces only as "cannot connect").
