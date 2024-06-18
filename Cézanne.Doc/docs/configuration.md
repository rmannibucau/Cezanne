---
uid: configuration
---

# Configuration

This part is about the shared configuration, i.e. the configuration any command can use.

It is taken either from the environment variables (`CEZANNE_` prefix), the command line (`--xxx`) or `cezanne` section of `cezanne.json` configuration file (it uses Microsoft configuration extension under the hood).

The two main configuration are related to:

* Kubernetes client (sections `cezanne>kubernetes` in `cezanne.json`)
* Maven repositories (sections `cezanne>maven` in `cezanne.json`)

> [!TIP]
> Because we use Microsoft configuration binder, the separator between section/properties in the environment names are not `_` but `__`, ex: `CEZANNE__KUBERNETES__KUBECONFIG`. Similarly, the integration with the command line arguments does not use `-` as separator but `:`, ex: `--cezanne:kubernetes:KubeConfig=...`

[!INCLUDE [Reference](./generated/configuration/properties.json)]
