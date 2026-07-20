---
argument-hint: [issue]
description: Fix a specific issue in the project.
---
Fix issue $1 in the project. This argument should be in the format of a GitHub issue number, i.e. "6" or "#6".
Note that "issue" here means GitHub issue, not necessarily a "problem" in the code. The issue may be a feature request, a bug report, or any other type of issue.
Check out a new branch named `issue/$1` (remove any "#" prefix) from the `main` branch.
Make sure to pull the latest changes from `main` before creating the new branch.
Use the `gh` GitHub CLI to get the details of issue $1.
Analyze the issue details and determine the necessary code changes to fix the issue.
Try to use test-driven development (TDD) to implement the fix, writing tests for the expected behavior before making code changes.
Make the required code changes to fix the issue, ensuring it meets project standards and patterns, and has good unit/integration test coverage.
For changes that impact the interaction with the database, make sure it has at least one full round-trip integration test.
Run the project's full test suite to ensure that the changes do not introduce any new issues.
If all tests pass, commit the changes with a message referencing issue $1.
Push the branch to the remote repository and create a pull request targeting the `main` branch.
Give the user a summary of the changes made and how they address the issue in the pull request description.
