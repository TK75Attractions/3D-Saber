# Mac iOS hot-path harness

Run `python3 PhoneSaber/tools/ios-hot-path/run.py --frames 240` from the Git root.
Use `--revision origin/main` for the original source, or `--source-root` for
another worktree. A temporary directory holds all compiled artifacts.

See [measurement details](../../docs/claude/ios-frame-alloc.md) for the measured
stages, allocation-counter semantics, limitations and baseline results.
