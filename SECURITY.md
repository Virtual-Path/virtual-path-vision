# Security Policy

## Supported Versions

| Version | Supported |
|---|---|
| 3.x (current) | :white_check_mark: |
| < 3.0 | :x: |

## Reporting a Vulnerability

**Please do not open a public issue for security problems.**

Report suspected vulnerabilities privately via GitHub's
[Security Advisories](https://github.com/xianshi3/machine-vision-app/security/advisories/new)
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
