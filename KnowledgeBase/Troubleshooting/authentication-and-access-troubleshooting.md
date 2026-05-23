# Authentication And Access Troubleshooting

## Typical signals

- login fails after deployment
- access denied after role or permission change
- user session expires unexpectedly
- SSO or external identity synchronization appears stale

## Verification order

1. Confirm whether the issue affects one user or multiple users.
2. Confirm whether the issue is limited to one browser or environment.
3. Check recent deployment, configuration, or access synchronization changes.
4. Compare with prior resolved tickets that mention authentication, authorization, SSO, or access sync.

## Likely operational paths

- one user only: validate role, account state, and recent access change
- multiple users: validate service-side configuration, sync process, or deployment state
- browser-specific: reproduce with another browser and compare session behavior

## Recommended support response pattern

Use the strongest historical case first, then verify the documented access flow before escalating to engineering.
