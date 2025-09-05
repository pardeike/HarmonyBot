# HarmonyBot rules for Codex

* This is the only AGENTS.md file in the repository

* dotnet (.NET 9) should be installed, if not found the dotnet root is: /root/.dotnet

* Respect .editorconfig when doing edits, especially for using whitespace

* Use the latest language features. Use `var` and the shorter array syntax. Longer than usual lines (~ 140 chars are ok)

* Don't bother with whitespace formatting during work. Instead, just before you are done, use `dotnet format` to format the code - it will respect the .editorconfig file.
