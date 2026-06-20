# Coding style guideline

For conciseness, logical **clarity**, and maintainability:

1. Code **documentation** should only include the following content:
    1. Interface contract
       - All functions must be documented with full type annotations, along with their parameters, return values, common exceptions, and notable side-effects.
       - Only the interface contract is strictly necessary for all code **constructs**. Try your best **not to include** other items.
    2. Summary of Purpose
       - Should only be used for complex code constructs such as classes and modules in the vast majority of cases.
       - Should be used for simpler constructs such as functions **if and only if** the construct name is not self-explanatory.
    3. Usage example
       - Is **only** needed where the code construct in question has complex or counterintuitive behavior.
    4. Remarks
       - Should only be used to convey important information related to business logic, such as architectural information or rationale behind design decisions.
       - **Is not** a place to put random notes.
       - **Not to be confused with inline comments** (see item 3).
2. Code **documentation** should only focus on external behavior, and **NOT** implementation **details**.
3. Comments should only be used to note complex or subtle business logic. **IT IS NOT FOR MAKING RANDOM NOTES UNRELATED TO BUSINESS LOGIC**, nor for stating the obvious.
4. Always prefer abstraction over complex logic flow.
5. Always consider adding abstraction layer instead of adding a new branch.
   - However, this applies *when the branching condition represents a conceptual category that may grow or change independently* (e.g., file type processing strategies, upload mode strategies). If there are only 2–3 simple branches with low likelihood of expansion, keep it simple.
6. For **any** function with more than 3 parameters, it is at risk of being overcomplicated. Functions with more than 5 parameters **must** be refactored. Always consider refactoring into a class or using a parameter pack object.
7. For **any** function with more than 2 nested layers of basic logic constructs (`if`/`elif`/`else`, `for`, `while`, `try`/`except`/`finally`, `with`, `match`/`case`), or containing more than 3 such constructs, it is at risk of being a long method. Always consider refactoring it by breaking it into smaller pieces or transforming it into a class.
8. For **any** function with more than 1 nested function, it is at risk of being a long method. Always consider transforming it into a class.
9.  **Error handling strategy**
    - Internal logic errors should propagate as exceptions to the caller.
    - External interfaces should catch and translate exceptions into structured error responses — prefer a shared helper to avoid repetitive `try/except` blocks across tools.
10. **Async conventions**
    - I/O-bound operations → Run on the calling thread;
    - CPU-bound pure computations → Run on thread pool;
    - Use Synchronization context to sync continuations as needed if it is available.
    - Be mindful of shared mutable state across coroutines — document or guard against race conditions.
11. **Shared mutable state**
    - Instance-level mutable state (e.g., accumulators, phase trackers) must be documented with thread/coroutine safety considerations.
    - If a method modifies shared state, that is a **side-effect** and must be declared in the interface contract (see item 1.1).
12. **DO NOT HANDCRAFT COMMANDS WITH STRING CONCAT**
    - **Always** use parameterized query
    - **Always** use script function with parameters

## Notable exceptions

Interface from external dependencies might have different requirements and styles, especially:

- Database interfaces
- Independent providers this project depends on

In this case, follow the requirement of said dependencies.

# Language Usage Guideline

1. Always use English for source code documentation and inline comments.
   - If the programming language/dev tool supports documentation localization, always produce a Chinese localization file.
2. For all other files, including but not limited to design documents and agent prompts:
   1. A working document uses English and Chinese as working languages, where:
       - Always use Chinese in conceptual content, such as architectural design, data flows, and relationships.
       - Always use English in rules, guidelines, and logical content.
   2. Always produce a Chinese and/or an English translation document based on the working document as needed. For example, if a working document is fully English, produce a Chinese translation. For another example, if a working document is written in mixed language, produce both translations.
   3. In the comparison between the working document and the translation, the working document shall prevail.

# Work Log Guideline

## TODO

A flat, single-level checklist updated as work progresses. Check off items
(`- [x]`) when completed. Add new items as they arise. The TODO file lives
in the job folder alongside other work logs.

```
TODO.md
- [x] Task A
- [ ] Task B
- [ ] Task C
```

The TODO is a **living document** — review and check items after each entry
in the work note, not just at the end of the session.

## Work Notes

Work notes are **real-time progress logs** written during a
work session. They serve as a granular, timestamped record of actions taken,
decisions made, and findings encountered — supplementing the planning document
(`Testing Plan.md`), task tracker (`TODO.md`), and retrospective report
(`Work Summary.md`) that live alongside them in the same job folder.

### Note format

#### File naming

```
YYYY-MM-DD.HHmm-HHmm.Short-Description.md
```

One file = one continuous work session. A workday may contain 1–5 sessions.

#### Frontmatter

Every note file must begin with a YAML frontmatter block:

```yaml
---
session_start: 2026-06-20T09:30:00+00:00
session_end: 2026-06-20T18:30:00+00:00
tags: [tag1, tag2, tag3]
revisit:
  - "Component X requires further testing"
  - "YYY.cs line 123 - better async handling"
files:
  - src/**/*.ext
---
```

Frontmatter fields:

| Field | Required | Purpose |
|-------|----------|---------|
| `session_start` / `session_end` | Yes | Precise time range; enables chronological queries |
| `tags` | Yes | Keywords for categorization and search |
| `revisit` | No | Accumulating list of items to revisit in a future cycle |
| `files` | No | Source files touched during this session |

#### Body structure

The body is organized into **entries** — each entry is a self-contained unit
modeled after a small GitHub issue. An entry may describe a task, a bug fix,
a design decision, an exploration, a problem encountered, or any other
meaningful unit of work.

```markdown
# [dd HH:MM] Brief session title

## Context
Background and motivation for this session. 1–2 paragraphs.

## Entries

### Short descriptive title
> time: [dd HH:MM] · status: resolved | deferred | blocked | in-progress | discarded | ... · tags: bugfix, driver, pgvector, ...

Free-form body. Cross-reference source files, design documents, companion
files (`Testing Plan.md`, `TODO.md`, `Work Summary.md`), or other entries.

### Another entry
> time: [dd HH:MM] · status: deferred · tags: placeholder, blob

Body text...

## Delta
Quantifiable changes produced during this session.

## Next
Immediate follow-up tasks. Bullet list.
```

### Entry metadata rules

Each entry must start with a `>` line containing `time`, `status`, and `tags`,
separated by `·` (U+00B7).

| Status | When to use |
|--------|-------------|
| `resolved` | Task completed, bug fixed, decision made |
| `deferred` | Acknowledged but intentionally postponed to a later cycle |
| `blocked` | Cannot proceed because of an external dependency |
| `in-progress` | Currently being worked on |
| `discarded` | Explored and rejected |

Tags are lowercase, hyphen-separated. Reuse existing tags when possible.

### Timetamp Rule

- **Always** use UTC+0 for any timestamp format that does not include timezone information.
- **Always** use script to print timestamp. **Never** write it or generate it.
- You may use `----` as placeholder for timestamps that can not be decided at the moment. e.g. "Work ended". And replace it with the proper timestamp afterwards.

### Relationship with other files

| File | When | Role |
|------|------|------|
| `Testing Plan.md` | Before work | Outlines goals and approach |
| `TODO.md` | During work | Task tracking; updated as items are completed or added |
| Work note | **During work** | **Real-time progress record** |
| `Work Summary.md` | After work | Retrospective engineering report |

The work note is **not** a summary and **not** a replacement for any of the
above. It is an auxiliary record that provides context the other files cannot:
timestamps, step-by-step actions, intermediate findings, and the raw timeline
of decisions.

### Style rules

1. Write entries as work progresses, not after the fact.
2. Keep entries short and focused. If an entry exceeds ~200 lines, consider
   splitting it into multiple entries.
3. Use `[file:path]` to reference source files. Use `[entry:title]` to
   cross-reference other entries within the same file.
4. Do not repeat information already recorded in `Testing Plan.md` or
   `TODO.md` — reference them instead.
5. The `revisit` field in frontmatter should be updated at the end of each
   session: add new items, remove resolved ones. This keeps it as a live
   tech-debt accumulator.


## Work Summary

A "Work Summary" must be structured as a proper engineering report. At a minimum, it must contain the following major sections:

1. A table of contents (TOC)
2. Summary
3. Background
4. Plan
5. Work performed
6. Unfinished work


### Section descriptions

**Table of contents**  
Provide a nested numbered list that indexes all sections and subsections in the report. Do not use numbering in the section titles themselves.

**Background**  
Summarize the project's status prior to this work, highlighting any specific points of interest that motivated or influenced the current effort. Clearly state the goals and expected outcomes of this work.

**Plan**  
Give a concise summary of the intended approach. For the detailed breakdown, reference a separate `TODO.md` file (or equivalent task-tracking system).

**Work performed**  
Provide a detailed, factual record covering two distinct aspects:
- **Output**: Describe exactly what was produced during this work (e.g., code, designs, tests, documentation).
- **Discoveries**: Document all findings made during the work, including but not limited to poor design choices, unforeseen difficulties, and issues that were identified but intentionally left unresolved in this cycle.

**Unfinished work**  
Based on the outputs and discoveries listed above, clearly enumerate what tasks or issues remain to be addressed in the future.

**Summary**  
Write a 1–2 paragraph condensation of the entire report **after you finish writing everthing else**. Place this summary immediately after the table of contents and before the Background section.

### Formatting notes

1. You may introduce subsections within any major section to improve organization and readability.
2. You may choose alternative titles for the major sections, provided the content framework described above remains intact.
3. To simplify maintenance and editing, **do not** include numbered prefixes (e.g., "1.", "2.1") in any section or subsection titles. All indexing must be handled exclusively through the table of contents, which must be formatted as a nested numbered list.