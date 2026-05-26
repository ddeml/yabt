# Working With Codex

This file is a lightweight guide for the human side of the collaboration.

## Useful Ways To Steer Me

Be blunt about intent. Examples:

```text
Do not implement this yet; only add interfaces and docs.
Keep this Windows-first but do not block Linux later.
Prefer boring C# here.
Treat this as a PR review comment and make the smallest fix.
```

Give feedback incrementally. Small review comments are ideal because I can make focused changes, verify them, and keep the diff understandable.

Tell me when something is architectural memory. If a comment should shape future work, say so and I can add it to `AGENT.md`.

## Helpful Review Patterns

Good prompts during review:

```text
This project boundary feels wrong. Rework it so Core has no storage assumptions.
This name will age badly. Suggest alternatives before changing it.
Add tests around this behavior, but do not expand the implementation.
Explain the tradeoff, then make your recommended change.
```

## When To Ask For A Plan

Ask for a plan when the change touches architecture, public formats, or multiple projects.

Ask me to implement directly when the fix is local and obvious.

## What I Am Good At

- Repetitive project plumbing.
- Keeping cross-file changes consistent.
- Writing first-pass docs and schemas.
- Refactoring with constraints.
- Summarizing diffs and surfacing risks.
- Turning review comments into small patches.

## What To Watch Closely

- Public format decisions.
- Naming that encodes future assumptions.
- Accidental over-abstraction.
- Hidden coupling between projects.
- Anything that would make the archive less inspectable.

Those are worth your experienced human eye. I can help carry the details; you keep the taste and direction.
