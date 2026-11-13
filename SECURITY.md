# Security Policy

## Supported versions

The project is in preview. Security fixes are made on `main` and included in the next release; older preview versions are not patched.

## What counts

Zep is an editor-only tool that reads, moves, renames, imports, compresses and deletes files in a Unity project. Problems worth reporting privately include:

- a file operation that can write, move or delete outside the project it was invoked on (path traversal, symbolic-link following, archive extraction);
- code or command execution triggered by opening a project, importing a file or dropping content on the panel;
- package data cleanup removing files it does not own.

Ordinary bugs, including data loss that needs the user to perform the destructive action themselves, belong in the public [issue tracker](https://github.com/van-unity/zep/issues).

## Reporting a vulnerability

Please do not open a public issue. Use GitHub's private reporting instead: on the repository's **Security** tab choose **Report a vulnerability**.

Include the package commit or version, the Unity version and operating system, and the steps or a minimal project that reproduces the problem.

You can expect an acknowledgement within a week. Once a fix is available it is released and credited to you in the changelog, unless you prefer otherwise.
