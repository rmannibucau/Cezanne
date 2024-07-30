---
uid: manifest-reference
---

# Manifest Reference

You can reference the manifest JSON-Schema in your `manifest.json` files by adding the attribute `$schema` to your descriptor:

```json
{
    "$schema": "https://rmannibucau.github.com/Cezanne/docs/generated/schema/manifest.jsonschema.json"
}
```

> [!NOTE]
> For compatibility with Yupiik BundleBee, `alveoli` attribute is supported instead of `recipes` but is not mentionned in the schema for now.


Here is its content:

[!code-json[](./generated/schema/manifest.jsonschema.json)]
