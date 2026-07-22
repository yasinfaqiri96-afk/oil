---
name: ptg-release-deployment-guard
description: Use only when the user explicitly asks to release, publish, deploy, update the server, switch production, replace a production database, restart the PTG service, or prepare a verified deployment and rollback. Do not trigger for ordinary local builds, local runs, or code-only analysis.
effort: high
---

# PTG Release and Deployment Guard

Deploy as a reversible release, not as an in-place file copy.

## Authority and safety

- A deployment request authorizes the requested application/server workflow, not unrelated infrastructure or database replacement.
- Preserve production data. Replace or restore a database only when the user explicitly requests it and only after a verified backup.
- Never print or persist credentials, private keys, tokens, cookies, connection strings, or plaintext control-panel passwords.

## Preflight

1. Confirm repository, branch/commit, remote state, working tree, target host, service name, release path, runtime, and health URL from live evidence.
2. Identify migrations and configuration changes. Explain production impact before applying them.
3. Run the required Release build and relevant or full tests according to change risk.
4. Create a deployment artifact without local secrets or development files.

## Reversible deployment

1. Back up the current release and database when data or schema is in scope.
2. Upload to a new staging or release directory.
3. Verify artifact completeness, ownership, permissions, configuration presence, and runtime compatibility.
4. Apply approved migrations with a recorded recovery path.
5. Atomically switch the active release, restart the exact service, and avoid editing the live directory in place.
6. Verify service active state, health endpoint, login or page response, logs, and one critical read-only workflow.
7. Roll back the release and database when health checks fail; retain the previous known-good release.

## Final report

State deployed commit, artifact/runtime, backup paths without secrets, migration status, service/health results, rollback target, and any verification not performed.
