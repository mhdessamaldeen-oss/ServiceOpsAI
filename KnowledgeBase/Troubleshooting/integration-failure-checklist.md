# Integration Failure Checklist

## Common indicators

- external system timeout
- credential mismatch
- missing callback or webhook response
- data not synchronized after successful local action

## Verification order

1. Confirm the external system name and reference identifiers.
2. Check whether the issue started after a release or configuration change.
3. Confirm whether the issue affects one entity, one environment, or all traffic.
4. Review similar resolved tickets mentioning the same integration or external system.

## Evidence-backed next steps

- verify credentials and connectivity first
- verify environment mapping and callback configuration next
- use prior resolved incidents to avoid repeating low-value checks

## Escalation condition

Escalate when the issue is reproducible, affects multiple transactions or users, and has already passed basic credential and configuration checks.
