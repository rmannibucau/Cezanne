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
