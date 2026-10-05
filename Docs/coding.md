# Coding

A **coding surface** is a conversation between Lane and [Claude Code](https://code.claude.com), working together in one
directory. Whatever Lane says there is sent to Claude Code as an instruction. When Claude Code finishes a turn, its
reply comes back to Lane as a message from "Claude Code", with a short list of what it did.

Coding surfaces come from two places:

- **Configured workspaces** in `Lane:Coding:Workspaces`. One of them can be Lane's own source, which lets her rebuild
  and restart herself.
- **Projects Lane opens herself** from her monologue, with `open_coding_surface`. Each one is a new directory and git
  repository under `WorkspacesRoot`. They are remembered across restarts until she closes them.

---

## Requirements

- The `claude` CLI, signed in. Claude Code runs headless (`claude -p`), using whichever login the machine has. To use
  an API key instead, put `ANTHROPIC_API_KEY` in Lane's environment; it is on the `PassEnvironment` list by default.
- `git`.
- A GitHub personal access token, only for pushing to GitHub and reading issues.

## Configuration

Coding is off until `Lane:Coding:Enabled` is true.

```jsonc
"Coding": {
  "Enabled": true,
  "ClaudePath": "claude",
  "Model": null,                        // passed to --model when set, e.g. "sonnet"
  "WorkspacesRoot": "~/lane-workspaces",
  "GitHubTokenRef": "env:GITHUB_TOKEN",
  "GitAuthor": "Lane <lane@localhost>",
  "PermissionPort": 5085,
  "PermissionTimeout": "00:10:00",
  "MaxBudgetUsd": 5,
  "MaxUnattendedExchanges": 20,
  "AutoAllow": ["Read", "Glob", "Grep", "Edit", "Write", "Bash(dotnet build *)", "Bash(dotnet test *)"],
  "AlwaysDeny": ["Bash(git push *)", "Bash(rm -rf *)", "Bash(sudo *)"],
  "PassEnvironment": ["ANTHROPIC_API_KEY", "CLAUDE_CODE_OAUTH_TOKEN"],
  "Workspaces": [
    { "Id": "self", "Name": "Lane", "Path": "~/src/lane", "Purpose": "Lane's own source code.", "SelfHosted": true }
  ]
}
```

| Setting | Meaning |
|---|---|
| `Workspaces[].Id` | Letters, digits, `-` and `_`. The conversation is `coding.{Id}/Text/claude`. |
| `Workspaces[].SelfHosted` | This directory is Lane's own source. Enables `restart_self`. |
| `Workspaces[].GitHubTokenRef` | Overrides the global token for this workspace. Projects Lane opens use the global one. |
| `MaxBudgetUsd` | Passed to Claude Code as `--max-budget-usd`, per process. `0` means no cap. |
| `MaxUnattendedExchanges` | How many Claude Code replies Lane is handed in a row, with nobody else speaking, before the conversation pauses. |
| `AutoAllow`, `AlwaysDeny` | Claude Code permission rules, passed as `--allowedTools` and `--disallowedTools`. Leave a list out to get its default; write `[]` to empty it. |
| `PassEnvironment` | Variables Claude Code may inherit even though their names look like secrets. Every other inherited variable whose name contains `KEY`, `TOKEN`, `SECRET` or `PASSWORD` is removed before Claude Code starts. |

## Permissions

Claude Code asks before doing anything that is not covered by `AutoAllow`, and refuses whatever matches `AlwaysDeny`.
The question goes to Lane rather than to a terminal. Lane hosts a small MCP server on `127.0.0.1:PermissionPort`, and
Claude Code is started with `--permission-prompt-tool` pointing at it. Each workspace's Claude Code gets its own bearer
token.

A request shows up in the conversation as:

```
[Permission request hqxbp] Claude Code wants to use Bash: `echo hi > hello.txt` (Create hello.txt)
Answer with answer_claude_permission.
```

Lane answers with `answer_claude_permission`. A request nobody answers within `PermissionTimeout` is denied. The
outcome is recorded in the conversation either way.

Lane's answers come from whatever model is bound to the `respond` role. Keep `AutoAllow` to things you are happy to
have happen without anyone looking, and keep `AlwaysDeny` firm.

## Tools

| Tool | Where | What it does |
|---|---|---|
| `claude_status` | coding conversations | Busy or idle, cost so far, permission requests waiting, and `git status`. |
| `claude_interrupt` | coding conversations | Stops Claude Code's current turn. |
| `claude_new_session` | coding conversations | Ends the Claude Code conversation and starts a fresh one, optionally with an opening message. |
| `answer_claude_permission` | coding conversations | Allows or denies a permission request by its id. |
| `git` | coding conversations | `status`, `diff`, `log`, `commit` (stages everything first), `branches`, `switch`, `pull`, `push`, `set_remote`. There is no force-push, reset or clean. |
| `github_issues` | coding conversations | Lists a repository's issues or views one with its comments. Needs a token and a GitHub `origin`. |
| `restart_self` | Lane's own workspace | Rebuilds Lane and restarts her onto the new build, only if the build succeeds. |
| `open_coding_surface` | monologue | Creates a new project and its conversation. |
| `close_coding_surface` | monologue | Ends a project's conversation. Its directory is kept. |

The monologue sees coding conversations in its list of open conversations, and briefs Claude Code by speaking into one
with `speak_to_session`.

## GitHub

Lane commits as `GitAuthor` and pushes with the token. The token reaches git through `GIT_CONFIG_*` environment
variables, so it is never written to `.git/config` or visible in the process list. Claude Code never gets it.

For a fine-grained token, grant **Contents: read and write** to push and **Issues: read** to read issues, on the
repositories Lane should reach. A classic token needs `repo`.

## Watching from the dashboard

Click a conversation in the dashboard's session list to read it and to post into it as yourself. Messages posted there
come from the account `dashboard:owner`, named `Lane:Dashboard:OwnerName`; link that account to yourself under
`Lane:Identities`. The Coding panel shows each project's Claude Code state, cost, and any permission request waiting on
Lane.

Posting into a coding conversation also resumes it if it was paused by `MaxUnattendedExchanges`, and hands Lane the
Claude Code reply that was held. Remember that everything Lane says in a coding conversation goes to Claude Code,
including replies meant for you.

## Restarting herself

`restart_self` is offered only in the conversation for a workspace with `SelfHosted: true`. It:

1. builds `Lane.slnx` in that directory, and stops if the build fails, reporting the errors;
2. publishes `Lane.Host` to `.staging/` beside the running binary;
3. lays the new files over the running build one by one, keeping each replaced file in `.rollback/`;
4. writes `restart.json` and exits with code `75`, so the service manager starts the new build.

The running instance's `appsettings.json`, `appsettings.local.json`, `.env` and `lane.db` are never replaced. A change
that needs new configuration has to be made to those files by hand.

When the new build starts, Lane is told in that conversation which commit she is running. If the new build fails to
start `MaxFailedStarts` times in a row, the next start puts the previous build back from `.rollback/`, and Lane is
told that when she comes back. A start counts as a success once it has stayed up for `HealthyAfter`.

```jsonc
"Restart": {
  "Mode": "Auto",          // Supervisor under systemd or the com.lane.bot launchd job; Relaunch otherwise
  "ExitCode": 75,
  "Configuration": "Release",
  "MaxFailedStarts": 2,
  "HealthyAfter": "00:01:00"
}
```

This needs Lane to run as a service, installed with `Deploy/install.sh --service`. The launchd job restarts on any
exit, and the systemd unit sets `RestartForceExitStatus=75`. Started any other way, `restart_self` still builds and
reports the result, but does not replace anything. The dashboard's restart button follows `Mode` too.

The source directory is the checkout `install.sh --source` built from. The workspace `Path` should point at it.
