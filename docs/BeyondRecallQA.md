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
-br-qa-target-data <absolute targets.json inside the output directory>
-br-qa-target-identity <SHA-256 of the exact targets.json bytes>
-br-qa-exit-on-complete
```

Unknown, duplicate, incomplete, relative, traversing, and symlinked QA inputs are rejected. The secret must be below the declared external secrets root and have Unix mode `0600`. Credentials are read only for the requested login, are never copied into settings or event details, and `saveaccount`, `autologin`, and `reconnect` must all remain false.

Plans use strict schema version 2, at most 512 actions, at most 900 seconds total, and a closed action vocabulary. In addition to the foundation login lifecycle, Phase 0C supports bounded acknowledged paths, speech, skills and spells, serial/location targets, context menus, gump responses, vendor buy/sell packets, drag/drop, secure-trade responses, wait predicates, and stock custom-house operations. Each action has a fixed typed payload; unknown actions and fields are rejected. The only server command remains the foundation `[BrQaGump` check; ModernUO commands use an opening prefix and no closing bracket. The runner does not execute scripts, shell commands, arbitrary server commands, or arbitrary code.

Dynamic serials, paths, gump selectors, and button/context identifiers come only from the companion target document. That document is byte-hash bound to the launch, expires within 15 minutes, and is bound to the QA session, ModernUO deployment/source, Phase 0C manifest, legal data, prepared client, and build-injected TazUO identities. Actions reference safe aliases rather than accepting raw serials from a plan. A target can be resolved by its signed serial or, for a disposable dynamic world object such as a placed house sign, by bounded graphic/location metadata. A secure-trade alias may bind only when exactly one live trade window exists; its server-provided container serial is then reused for the paired response. The bounded `wait-item-parent` predicate distinguishes an item merely visible inside a trade window from the same signed item actually observed beneath the intended backpack.

The two-client smoke runs two separately configured native processes and may use `logout` followed by the normal authenticate/select/enter-world actions. Logout clears the QA session's observed login-handshake state, so a later action must receive fresh authentication and character-entry hooks. Completed journal barriers likewise clear their expectation and cannot satisfy a later barrier with stale evidence.

The Phase 0C action vocabulary is intentionally transactional but not authoritative. For example, `vendor-buy` records that a correctly encoded response was sent, and `accept-trade` records the response selection; neither event claims the server delivered an item or completed a trade. ModernUO's signed scenario host observes the actual state transition, save/restart result, and cleanup before the campaign can pass.

`qa-events.jsonl` is atomically replaced after each append. Events have a stable sequence and strictly increasing UTC evidence timestamps; a backward or duplicate wall-clock observation is clamped to one tick after the prior event without changing sequence order. Events carry the session, process/start time, endpoint, client version, legal-data identity, preparation build identity, and build-injected TazUO source/build identities. Asset, authentication, character, movement, gump, journal, target, vendor, context-menu, combat, drag/drop, trade-response, public-travel location, and housing milestones are emitted only when the corresponding client hook or packet action occurs. Journal text is never copied into the event stream; a matched value is represented by its SHA-256 digest. Input-sent events never fabricate server transaction success.

Beyond Recall builds inject `BeyondRecallSourceCommit` and `BeyondRecallBuildIdentity` assembly metadata during native publish. A consumer must reject QA evidence whose identities do not match the selected build provenance.
