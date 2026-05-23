# Known Issue: Repeated Access Sync Failure

## Pattern

Users report login or access failure shortly after a deployment or permission update. Similar incidents often involve delayed synchronization between the application and the external identity or authorization source.

## Matching signals

- repeated access denied after account change
- issue affects multiple users in the same environment
- problem appears after deployment or role update
- prior ticket history mentions sync, role propagation, or stale session state

## Recommended support handling

1. Compare the ticket with resolved access-sync incidents.
2. Verify synchronization job status or last successful propagation point.
3. Validate whether the issue clears after sync completion or forced revalidation.

## Escalation threshold

Escalate when the same pattern appears across multiple users and the expected synchronization path did not restore access.
