# Repository artifacts

Keep only durable documentation in `docs/`. Document current behavior, architecture, reproducible checks, and known limits. Do not retain session reports, handoffs, dated trial logs, or superseded plans there.

Put temporary code, probes, screenshots, logs, and investigation notes in an ignored `artifacts/<task>/` directory. Use a separate directory for each task. Do not add temporary helpers to production source or test projects.

After all subagents for a task finish, the parent must integrate useful code and regression tests, move lasting contracts into existing guides, and delete that task's scratch artifacts. Remove stale links when deleting documents. Keep results in the commit or PR rather than adding a session-summary document.

Before cleanup in a shared checkout, identify active threads and their owned files. Preserve their changes, scratch directories, and referenced inputs. Do not use blanket `git clean` or discard unfamiliar uncommitted code. Preserve native build dependencies in `artifacts/native/`.
