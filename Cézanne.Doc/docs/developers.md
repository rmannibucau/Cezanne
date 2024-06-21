---
uid: developers
---

# Developers

## Remote debugging

Cezanne runner (_Main_) supports the environment variables `CEZANNE_DEBUG` to start and await a debugger to attach to the started process.
It eases remote debugging when relevant.
To enable it, just set it to `true`.

```bash
CEZANNE_DEBUG=true ./cezanne ....
```

## Formatting

Jetbrains `cleancode` command works well and supports `.editorconfig`, however it is insanely slow so we prefer to use `dotnet csharpier .`.

See link:https://csharpier.com/docs/About[CSharpier] documentation for details.
