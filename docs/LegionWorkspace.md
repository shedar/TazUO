# External Legion workspace

TazUO can keep user-authored Legion scripts and mutable Legion settings outside the
published application directory:

```text
-legionscriptspath <absolute-directory>
-legionsettingspath <absolute-file>
```

Both values must be absolute, non-traversing paths. Duplicate options, missing
values, relative paths, symlink escapes, a script-root file, or a settings-path
directory fail before the game starts. TazUO creates an absent script directory
and the settings file's parent directory. Startup diagnostics record the resolved
paths and whether each one was overridden.

When the options are absent, upstream-compatible defaults remain:

```text
<executable-directory>/LegionScripts
<executable-directory>/Data/lscript.json
```

The configured script root is authoritative for discovery, script creation and
recording, edits, rename/delete, ZIP scripts, `API.py`, generated C# helpers,
open/edit actions, error navigation, reload, hotkeys, and auto-start identity.
The configured settings file stores global/character auto-start and collapsed
group state. The Legion persistent-variable database is kept beside that file,
so it follows the same durable or isolated state boundary. Settings writes use a
flushed sibling temporary file followed by an atomic replacement, preserving the
previous file if commit fails.

Beyond Recall uses:

```text
/Users/shedar/Projects/beyond-recall/Automation/LegionScripts
/Users/shedar/Projects/beyond-recall/runtime/automation-state/lscript.json
```

Its normal interactive client launch passes those paths. Automated QA,
acceptance, and capture clients pass separate empty task-owned workspaces, so
personal scripts cannot execute in operator evidence runs.
