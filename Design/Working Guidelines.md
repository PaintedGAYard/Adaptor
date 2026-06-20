# Coding style guideline

Given that this is an **LLM** tool project, for conciseness, logical **clarity**, and maintainability:

1. Code **documentation** should only include the following content:
    1. Interface contract
       - All Python functions must be documented with full type annotations, along with their parameters, return values, common exceptions, and notable side-effects.
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

# Language Usage Guideline

1. Always use English for source code documentation and inline comments.
   - If the programming language/dev tool supports documentation localization, always produce a Chinese localization file.
2. For all other files, including but not limited to design documents and agent prompts:
   1. A working document uses English and Chinese as working languages, where:
       - Always use Chinese in conceptual content, such as architectural design, data flows, and relationships.
       - Always use English in rules, guidelines, and logical content.
   2. Always produce a Chinese and/or an English translation document based on the working document as needed. For example, if a working document is fully English, produce a Chinese translation. For another example, if a working document is written in mixed language, produce both translations.
   3. In the comparison between the working document and the translation, the working document shall prevail.

# Notable exceptions

Interface from external dependencies might have different requirements and styles, especially:

- Database interfaces
- Independent providers this project depends on

In this case, follow the requirement of said dependencies.