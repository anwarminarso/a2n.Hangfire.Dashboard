# CLAUDE.md

@AGENTS.md

## Claude Code notes

- The user communicates in **Indonesian**; reply in Indonesian. Code, comments, commit messages, docs and
  CHANGELOG stay in English.
- Confirm before `git commit` / `git push` (show the conventional-commit title + body first), and sign commits
  with `--gpg-sign`.
- Before a task that touches the original dashboard parity, Console/Tags/RecurringJobAdmin compatibility, the
  heatmap, or release/stress testing, read the matching `.kiro/steering/*.md` file listed above.
- For UI changes, verify in a running sample app (SampleApp with `--StorageProvider=InMemory` if no DB is up) and
  check both light and dark theme.
- On Windows the PowerShell tool can lag or queue output; for long commands, write output to a file and read it.
