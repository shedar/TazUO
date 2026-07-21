# Beyond Recall local QA protocol

TazUO includes a narrow opt-in protocol used by Beyond Recall's local `ShardTool`. It is disabled when no `-br-qa-*` option is present and is not a general automation or remote-control interface.

The complete launch contract is:

```text
-br-qa-plan <absolute plan.json inside the output directory>
-br-qa-output <absolute new local output directory>
-br-qa-session <32 lowercase hexadecimal characters>
-br-qa-secret-file <absolute external 0600 secret file>
-br-qa-secrets-root <absolute external secrets root>
-br-qa-data-identity <64 lowercase hexadecimal characters>
-br-qa-preparation-build-identity <64 lowercase hexadecimal characters>
-br-qa-exit-on-complete
```

Unknown, duplicate, incomplete, relative, traversing, and symlinked QA inputs are rejected. The secret must be below the declared external secrets root and have Unix mode `0600`. Credentials are read only for the requested login, are never copied into settings or event details, and `saveaccount`, `autologin`, and `reconnect` must all remain false.

Plans are schema-versioned JSON with bounded total/action timeouts and a fixed action vocabulary. The initial runner supports local connection/authentication, selection of an already bootstrapped named QA character, enter-world waiting, acknowledged movement, speech/journal round trips, paperdoll/backpack/skills actions, a server command plus gump wait, logout, and exit. It does not execute scripts, shell commands, or arbitrary code.

`qa-events.jsonl` is atomically replaced after each append. Events have a stable sequence and carry the session, process/start time, endpoint, client version, legal-data identity, preparation build identity, and build-injected TazUO source/build identities. Asset, authentication, character, movement, gump, and journal milestones are emitted only from corresponding client/network hooks. Journal text is never copied into the event stream; a matched value is represented by its SHA-256 digest.

Beyond Recall builds inject `BeyondRecallSourceCommit` and `BeyondRecallBuildIdentity` assembly metadata during native publish. A consumer must reject QA evidence whose identities do not match the selected build provenance.
