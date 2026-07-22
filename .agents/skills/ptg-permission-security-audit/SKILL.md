---
name: ptg-permission-security-audit
description: Use whenever PTG work touches authentication, authorization, RolesController, users, roles, AppPermissions, permission management, admin access, company or fiscal-year isolation, sensitive routes, anti-forgery, login, session, password handling, or a request to open or close access to a module.
effort: high
---

# PTG Permission and Security Audit

Keep authorization enforced on the server. Hiding a menu or button is not access control.

## Audit workflow

1. Inspect `git status` and identify the exact roles, permissions, controllers, actions, menus, APIs, and data scopes affected.
2. Trace both the allowed path and direct URL/API access.
3. Check authentication, authorization policy enforcement, anti-forgery on mutations, input validation, and ownership filters.
4. Check horizontal access: company, fiscal year, record owner, and predictable-ID or IDOR exposure.
5. Check privilege escalation: who may create roles, assign permissions, edit admins, disable users, or grant a permission they do not possess.
6. Check audit logging for sensitive mutations without recording secrets.

## Security rules

- Prefer default deny and least privilege.
- Enforce permissions in controllers/services, then mirror them in navigation and UI.
- Do not open a sensitive route or weaken validation without explicit approval.
- Never expose passwords, hashes, tokens, connection strings, cookies, private keys, or recovery codes.
- Preserve safe redirect/local-return validation and session protections.
- Do not hard-code a fixed role when the requirement is admin-manageable permission control.

## Verification

Run targeted authorization/security tests for allowed and denied users, direct URL access, cross-company/fiscal-year access, anti-forgery, and privilege escalation. Use browser testing when a real session is available, but report it separately from source/test verification.

## Final report

List affected permissions and routes, who gains or loses access, security tests, unresolved risks, and whether any sensitive route changed.
